using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace FlowForge.Api.Tests;

[Collection("Workflow API")]
public sealed class WorkflowEndpointTests(WorkflowApiFixture fixture)
{
    [Fact]
    public async Task Workflow_lifecycle_preserves_publication_and_excludes_archived_from_default_list()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "  Primeiro  ", description = "Exemplo" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var workflow = await Body(created);
        var path = created.Headers.Location!.OriginalString;
        Assert.Equal($"/api/workflows/{workflow["id"]}", path);
        Assert.Equal("Primeiro", workflow["name"]!.GetValue<string>());
        Assert.Single(workflow["versions"]!.AsArray());
        Assert.Empty(workflow["versions"]![0]!["nodes"]!.AsArray());

        using var edited = await client.PutAsJsonAsync(path, new { expectedRevision = Revision(workflow), name = "Editado", description = (string?)null });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        workflow = await Body(edited);
        using var saved = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        workflow = await Body(saved);
        using var published = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(workflow) });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        workflow = await Body(published);
        var publishedId = workflow["currentPublishedVersionId"]!.GetValue<string>();
        var historical = workflow["versions"]![0]!.ToJsonString();
        Assert.Equal("published", workflow["versions"]![0]!["status"]!.GetValue<string>());
        Assert.Null(workflow["draftVersionId"]);

        using var draft = await client.PostAsJsonAsync(path + "/drafts", new { expectedRevision = Revision(workflow) });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        workflow = await Body(draft);
        Assert.Equal(historical, workflow["versions"]![0]!.ToJsonString());
        Assert.Equal(2, workflow["versions"]!.AsArray().Count);
        Assert.Equal(workflow["versions"]![0]!["nodes"]![0]!["nodeId"]!.ToString(),
            workflow["versions"]![1]!["nodes"]![0]!["nodeId"]!.ToString());
        Assert.NotEqual(workflow["versions"]![0]!["connections"]![0]!["id"]!.ToString(),
            workflow["versions"]![1]!["connections"]![0]!["id"]!.ToString());
        using var cleared = await client.PutAsJsonAsync(path + "/draft", new { expectedRevision = Revision(workflow), nodes = Array.Empty<object>(), connections = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        workflow = await Body(cleared);
        using var version = await client.GetAsync(path + "/versions/" + publishedId);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(historical, (await Body(version)).ToJsonString());

        using var archived = await client.PostAsJsonAsync(path + "/archive", new { expectedRevision = Revision(workflow) });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        workflow = await Body(archived);
        Assert.NotNull(workflow["archivedAt"]);
        using var list = await client.GetAsync("/api/workflows");
        var page = await Body(list);
        Assert.Empty(page["items"]!.AsArray());
        using var inclusive = await client.GetAsync("/api/workflows?includeArchived=true&offset=0&limit=1");
        Assert.Single((await Body(inclusive))["items"]!.AsArray());
        using var mutation = await client.PutAsJsonAsync(path, new { expectedRevision = Revision(workflow), name = "Inalterável" });
        await Problem(mutation, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Invalid_publication_returns_rule_errors_and_keeps_revision_and_draft()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var response = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(workflow) });
        var problem = await Problem(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(problem["errors"]!.AsArray(), error => error!["code"]!.ToString() == "emptyGraph");
        using var detail = await client.GetAsync(path);
        var unchanged = await Body(detail);
        Assert.Equal(Revision(workflow), Revision(unchanged));
        Assert.NotNull(unchanged["draftVersionId"]);
        Assert.Null(unchanged["currentPublishedVersionId"]);
    }

    [Fact]
    public async Task Concurrent_edits_have_one_winner_and_do_not_overwrite_it()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(path, new { expectedRevision = Revision(workflow), name = "Cliente A" }),
            client.PutAsJsonAsync(path, new { expectedRevision = Revision(workflow), name = "Cliente B" }));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            await Problem(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
            var winner = await Body(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK));
            using var detail = await client.GetAsync(path);
            var stored = await Body(detail);
            Assert.Equal(winner["name"]!.ToString(), stored["name"]!.ToString());
            Assert.Equal(Revision(workflow) + 1, Revision(stored));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Owner_is_server_configuration_and_foreign_resources_are_not_visible_or_mutable()
    {
        var owner = Guid.NewGuid();
        await using var apiA = fixture.CreateApi(owner);
        await using var apiB = fixture.CreateApi();
        using var clientA = apiA.CreateClient();
        using var clientB = apiB.CreateClient();
        var (path, workflow) = await Create(clientA);
        clientB.DefaultRequestHeaders.Add("X-Owner-User-Id", owner.ToString());
        using var list = await clientB.GetAsync("/api/workflows?ownerUserId=" + owner);
        Assert.Empty((await Body(list))["items"]!.AsArray());
        using var get = await clientB.GetAsync(path);
        await Problem(get, HttpStatusCode.NotFound);
        using var version = await clientB.GetAsync(path + "/versions/" + workflow["draftVersionId"]);
        await Problem(version, HttpStatusCode.NotFound);
        using var edit = await clientB.PutAsJsonAsync(path, new { expectedRevision = Revision(workflow), name = "Ataque" });
        await Problem(edit, HttpStatusCode.NotFound);
        foreach (var operation in new[] { "archive", "publish", "drafts" })
        {
            using var response = await clientB.PostAsJsonAsync(path + "/" + operation, new { expectedRevision = Revision(workflow) });
            await Problem(response, HttpStatusCode.NotFound);
        }
        using var graph = await clientB.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        await Problem(graph, HttpStatusCode.NotFound);
        using var injection = await clientB.PostAsJsonAsync("/api/workflows", new { name = "Ataque", ownerUserId = owner });
        await Problem(injection, HttpStatusCode.BadRequest);
        using var original = await clientA.GetAsync(path);
        Assert.Equal(workflow.ToJsonString(), (await Body(original)).ToJsonString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"  \"}")]
    [InlineData("{\"name\":\"Teste\",\"unknown\":true}")]
    [InlineData("{\"name\":\"A\",\"name\":\"B\"}")]
    [InlineData("{")]
    [InlineData("null")]
    public async Task Invalid_create_transport_returns_problem_details(string json)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        using var response = await client.PostAsync("/api/workflows", new StringContent(json, Encoding.UTF8, "application/json"));
        await Problem(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("?offset=-1")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?limit=abc")]
    [InlineData("?includeArchived=maybe")]
    public async Task Invalid_pagination_returns_problem_details(string query)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        using var response = await client.GetAsync("/api/workflows" + query);
        await Problem(response, HttpStatusCode.BadRequest);
    }

    internal static object Graph(int revision)
    {
        var trigger = Guid.NewGuid();
        var log = Guid.NewGuid();
        return new
        {
            expectedRevision = revision,
            nodes = new object[]
            {
                new { nodeId = trigger, configuration = new { type = "webhookTrigger" }, position = new { x = 10.5, y = 20 } },
                new { nodeId = log, configuration = new { type = "log", message = "Recebido" } }
            },
            connections = new[] { new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = log, sourcePort = "next" } }
        };
    }

    internal static async Task<(string Path, JsonObject Workflow)> Create(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/workflows", new { name = "Teste" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (response.Headers.Location!.OriginalString, await Body(response));
    }

    internal static int Revision(JsonObject workflow) => workflow["revision"]!.GetValue<int>();
    internal static async Task<JsonObject> Body(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    internal static async Task<JsonObject> Problem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Body(response);
        Assert.Equal((int)expected, body["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["title"]!.GetValue<string>()));
        return body;
    }
}
