using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;

[Collection("Workflow API")]
public sealed class WorkflowContractTests(WorkflowApiFixture fixture)
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":-1,\"name\":\"Inválido\"}")]
    [InlineData("{\"expectedRevision\":1.2,\"name\":\"Inválido\"}")]
    [InlineData("{\"expectedRevision\":\"1\",\"name\":\"Inválido\"}")]
    [InlineData("{\"expectedRevision\":1,\"name\":null}")]
    [InlineData("{\"expectedRevision\":1,\"name\":\"A\",\"Name\":\"B\"}")]
    public async Task Invalid_edit_does_not_change_the_workflow(string json)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, original) = await Create(client);
        using var response = await client.PutAsync(path, Content(json));
        await Problem(response, HttpStatusCode.BadRequest);
        using var get = await client.GetAsync(path);
        Assert.Equal(original.ToJsonString(), (await Body(get)).ToJsonString());
    }

    [Theory]
    [InlineData("{\"type\":\"unknown\"}")]
    [InlineData("{}")]
    [InlineData("{\"message\":\"Sem discriminador\"}")]
    [InlineData("{\"type\":\"log\"}")]
    [InlineData("{\"type\":\"log\",\"message\":null}")]
    [InlineData("{\"type\":\"log\",\"message\":\"x\",\"schemaVersion\":1}")]
    [InlineData("{\"type\":\"delay\",\"durationTicks\":0}")]
    [InlineData("{\"type\":\"delay\",\"durationTicks\":864000000001}")]
    [InlineData("{\"type\":\"httpRequest\",\"url\":\"http://example.com\",\"method\":\"get\"}")]
    [InlineData("{\"type\":\"httpRequest\",\"url\":\"https://example.com\",\"method\":1}")]
    [InlineData("{\"type\":\"condition\",\"sourcePointer\":\"/x\",\"operation\":\"equals\"}")]
    [InlineData("{\"type\":\"condition\",\"sourcePointer\":\"/x\",\"operation\":\"exists\",\"expectedValue\":null}")]
    [InlineData("{\"type\":\"condition\",\"sourcePointer\":\"/x\",\"operation\":\"greaterThan\",\"expectedValue\":1e100}")]
    [InlineData("{\"type\":\"transformJson\",\"fields\":[{\"targetProperty\":\"x\"}]}")]
    [InlineData("{\"type\":\"transformJson\",\"fields\":[{\"targetProperty\":\"x\",\"sourcePointer\":\"\",\"literal\":null}]}")]
    [InlineData("{\"type\":\"transformJson\",\"fields\":[{\"targetProperty\":\"x\",\"literal\":{\"a\":1,\"a\":2}}]}")]
    [InlineData("null")]
    public async Task Invalid_node_configuration_is_rejected_without_saving(string configuration)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, original) = await Create(client);
        var graph = new JsonObject
        {
            ["expectedRevision"] = Revision(original),
            ["nodes"] = new JsonArray(new JsonObject { ["nodeId"] = Guid.NewGuid().ToString(), ["configuration"] = JsonNode.Parse(configuration) }),
            ["connections"] = new JsonArray()
        };
        using var response = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        await Problem(response, HttpStatusCode.BadRequest);
        using var get = await client.GetAsync(path);
        Assert.Equal(original.ToJsonString(), (await Body(get)).ToJsonString());
    }

    [Fact]
    public async Task Discriminator_can_follow_other_JSON_properties()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        var graph = new JsonObject
        {
            ["expectedRevision"] = Revision(workflow),
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["nodeId"] = Guid.NewGuid().ToString(),
                ["configuration"] = new JsonObject { ["message"] = "Olá", ["type"] = "log" }
            }),
            ["connections"] = new JsonArray()
        };
        using var response = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task All_six_configurations_roundtrip_through_HTTP_and_PostgreSQL_with_null_literals_and_exact_ticks()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        var configs = JsonNode.Parse("""
            [
              {"type":"webhookTrigger"},
              {"type":"httpRequest","url":"https://example.com/resource","method":"post"},
              {"type":"delay","durationTicks":1234567},
              {"type":"condition","sourcePointer":"/a~1b","operation":"equals","expectedValue":null},
              {"type":"transformJson","fields":[
                {"targetProperty":"root","sourcePointer":""},
                {"targetProperty":"empty","literal":null},
                {"targetProperty":"object","literal":{"nested":[1,true,"ação"]}}
              ]},
              {"type":"log","message":"Concluído"}
            ]
            """)!.AsArray();
        var nodes = new JsonArray(configs.Select(c => (JsonNode)new JsonObject
        {
            ["nodeId"] = Guid.NewGuid().ToString(), ["configuration"] = c!.DeepClone(),
            ["position"] = new JsonObject { ["x"] = -10.25, ["y"] = 42.75 }
        }).ToArray());
        var graph = new JsonObject { ["expectedRevision"] = Revision(workflow), ["nodes"] = nodes, ["connections"] = new JsonArray() };
        using var save = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var get = await client.GetAsync(path);
        var stored = (await Body(get))["versions"]![0]!["nodes"]!.AsArray();
        for (var i = 0; i < configs.Count; i++)
            Assert.True(JsonNode.DeepEquals(configs[i], stored[i]!["configuration"]));
        Assert.Equal(-10.25, stored[0]!["position"]!["x"]!.GetValue<double>());
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(save)) });
        var problem = await Problem(publish, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(problem["errors"]!.AsArray(), e => e!["code"]!.ToString() == "conditionBranchesIncomplete");
        Assert.Contains(problem["errors"]!.AsArray(), e => e!["code"]!.ToString() == "unreachableNode");
    }

    [Theory]
    [InlineData("duplicateNode", "duplicateNodeId")]
    [InlineData("duplicateConnection", "duplicateConnectionId")]
    [InlineData("missingSource", "missingSourceNode")]
    [InlineData("missingTarget", "missingTargetNode")]
    [InlineData("duplicatePort", "duplicateSourcePort")]
    public async Task Storage_invariants_return_rule_errors_before_SQL_and_preserve_draft(string kind, string code)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        var graph = System.Text.Json.JsonSerializer.SerializeToNode(Graph(Revision(workflow)))!.AsObject();
        var nodes = graph["nodes"]!.AsArray();
        var edges = graph["connections"]!.AsArray();
        if (kind == "duplicateNode") nodes.Add(nodes[0]!.DeepClone());
        if (kind == "duplicateConnection") edges.Add(edges[0]!.DeepClone());
        if (kind == "missingSource") edges[0]!["sourceNodeId"] = Guid.NewGuid().ToString();
        if (kind == "missingTarget") edges[0]!["targetNodeId"] = Guid.NewGuid().ToString();
        if (kind == "duplicatePort")
        {
            var second = edges[0]!.DeepClone();
            second["id"] = Guid.NewGuid().ToString();
            edges.Add(second);
        }
        using var response = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        var problem = await Problem(response, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(problem["errors"]!.AsArray(), e => e!["code"]!.ToString() == code);
        using var get = await client.GetAsync(path);
        Assert.Equal(workflow.ToJsonString(), (await Body(get)).ToJsonString());
    }

    [Theory]
    [InlineData("nodesNull")]
    [InlineData("nodeNull")]
    [InlineData("connectionsNull")]
    [InlineData("connectionNull")]
    [InlineData("tooManyNodes")]
    [InlineData("tooManyConnections")]
    [InlineData("emptyNodeId")]
    [InlineData("nonHttpCredential")]
    [InlineData("positionNull")]
    [InlineData("versionInjection")]
    public async Task Invalid_graph_transport_is_rejected(string kind)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        var graph = System.Text.Json.JsonSerializer.SerializeToNode(Graph(Revision(workflow)))!.AsObject();
        var nodes = graph["nodes"]!.AsArray();
        var edges = graph["connections"]!.AsArray();
        if (kind == "nodesNull") graph["nodes"] = null;
        if (kind == "nodeNull") nodes[0] = null;
        if (kind == "connectionsNull") graph["connections"] = null;
        if (kind == "connectionNull") edges[0] = null;
        if (kind == "tooManyNodes") while (nodes.Count < 51) nodes.Add(nodes[0]!.DeepClone());
        if (kind == "tooManyConnections") while (edges.Count < 101) edges.Add(edges[0]!.DeepClone());
        if (kind == "emptyNodeId") nodes[0]!["nodeId"] = Guid.Empty.ToString();
        if (kind == "nonHttpCredential") nodes[0]!["credentialId"] = Guid.NewGuid().ToString();
        if (kind == "positionNull") nodes[0]!["position"] = null;
        if (kind == "versionInjection") nodes[0]!["workflowVersionId"] = Guid.NewGuid().ToString();
        using var response = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        await Problem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Connection_ID_collision_with_another_workflow_is_a_conflict_and_rolls_back()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (pathA, workflowA) = await Create(client);
        var (pathB, workflowB) = await Create(client);
        var graph = Graph(Revision(workflowA));
        using var saveA = await client.PutAsJsonAsync(pathA + "/draft", graph);
        Assert.Equal(HttpStatusCode.OK, saveA.StatusCode);
        using var saveB = await client.PutAsJsonAsync(pathB + "/draft", graph);
        await Problem(saveB, HttpStatusCode.Conflict);
        using var getB = await client.GetAsync(pathB);
        Assert.Equal(workflowB.ToJsonString(), (await Body(getB)).ToJsonString());
    }

    [Fact]
    public async Task Reusing_a_published_connection_ID_is_rejected_and_historical_version_is_unchanged()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var save = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(save)) });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await Body(publish);
        using var createDraft = await client.PostAsJsonAsync(path + "/drafts", new { expectedRevision = Revision(published) });
        Assert.Equal(HttpStatusCode.Created, createDraft.StatusCode);
        var current = await Body(createDraft);
        var historical = current["versions"]![0]!;
        var graph = new JsonObject
        {
            ["expectedRevision"] = Revision(current),
            ["nodes"] = historical["nodes"]!.DeepClone(),
            ["connections"] = historical["connections"]!.DeepClone()
        };
        using var saveAgain = await client.PutAsync(path + "/draft", Content(graph.ToJsonString()));
        await Problem(saveAgain, HttpStatusCode.UnprocessableEntity);
        using var get = await client.GetAsync(path);
        Assert.Equal(current.ToJsonString(), (await Body(get)).ToJsonString());
    }

    [Fact]
    public async Task State_and_revision_conflicts_never_create_or_publish_extra_versions()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var duplicate = await client.PostAsJsonAsync(path + "/drafts", new { expectedRevision = Revision(workflow) });
        await Problem(duplicate, HttpStatusCode.Conflict);
        using var save = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var stale = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(workflow) });
        await Problem(stale, HttpStatusCode.Conflict);
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(save)) });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var current = await Body(publish);
        using var again = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(current) });
        await Problem(again, HttpStatusCode.Conflict);
        using var edit = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(current)));
        await Problem(edit, HttpStatusCode.Conflict);
        using var get = await client.GetAsync(path);
        Assert.Equal(current.ToJsonString(), (await Body(get)).ToJsonString());
        using var wrongVersion = await client.GetAsync(path + "/versions/" + Guid.NewGuid());
        await Problem(wrongVersion, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pagination_is_stable_and_does_not_repeat_items()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var first = await Create(client);
        var second = await Create(client);
        var third = await Create(client);
        using var all = await client.GetAsync("/api/workflows?limit=3");
        var items = (await Body(all))["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        for (var i = 0; i < 3; i++)
        {
            using var page = await client.GetAsync("/api/workflows?offset=" + i + "&limit=1");
            var body = await Body(page);
            Assert.Single(body["items"]!.AsArray());
            Assert.Equal(items[i]!["id"]!.ToString(), body["items"]![0]!["id"]!.ToString());
            Assert.Equal(i, body["offset"]!.GetValue<int>());
        }
        Assert.Equal(3, items.Select(i => i!["id"]!.ToString()).Distinct().Count());
        Assert.Contains(items, i => i!["id"]!.ToString() == first.Workflow["id"]!.ToString());
        Assert.Contains(items, i => i!["id"]!.ToString() == second.Workflow["id"]!.ToString());
        Assert.Contains(items, i => i!["id"]!.ToString() == third.Workflow["id"]!.ToString());
    }

    private static StringContent Content(string json) => new(json, Encoding.UTF8, "application/json");
}
