using System.Net;
using System.Net.Http.Json;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;

[Collection("Workflow API")]
public sealed class ExecutionListEndpointTests(WorkflowApiFixture fixture)
{
    [Fact]
    public async Task History_is_paged_newest_first_scoped_to_workflow_and_survives_archive()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var graph = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        var published = await Body(publish);
        var ids = new List<string>();
        for (var n = 0; n < 3; n++)
        {
            using var requested = await client.PostAsync(path + "/executions", null);
            ids.Add((await Body(requested))["id"]!.ToString());
        }
        var (otherPath, _) = await Create(client);
        using var empty = await client.GetAsync(otherPath + "/executions");
        Assert.Empty((await Body(empty))["items"]!.AsArray());

        using var first = await client.GetAsync(path + "/executions?limit=2");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = await Body(first);
        Assert.Equal(0, page["offset"]!.GetValue<int>());
        Assert.Equal(2, page["limit"]!.GetValue<int>());
        Assert.Equal(ids.AsEnumerable().Reverse().Take(2), page["items"]!.AsArray().Select(e => e!["id"]!.ToString()));
        var item = page["items"]![0]!.AsObject();
        Assert.False(item.ContainsKey("ownerUserId"));
        Assert.False(item.ContainsKey("triggerInputProtected"));
        Assert.False(item.ContainsKey("executionContextProtected"));
        Assert.Equal(published["currentPublishedVersionId"]!.ToString(), item["workflowVersionId"]!.ToString());
        Assert.NotNull(item["deadlineAt"]);

        using var second = await client.GetAsync(path + "/executions?offset=2&limit=2");
        Assert.Equal(ids[0], (await Body(second))["items"]![0]!["id"]!.ToString());
        using var tail = await client.GetAsync(path + "/executions?offset=3");
        Assert.Empty((await Body(tail))["items"]!.AsArray());
        using var archive = await client.PostAsJsonAsync(path + "/archive", new { expectedRevision = Revision(published) });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        using var history = await client.GetAsync(path + "/executions");
        Assert.Equal(3, (await Body(history))["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task Missing_and_other_owner_workflows_are_hidden_even_without_executions()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, _) = await Create(client);
        await using var other = fixture.CreateApi();
        using var otherClient = other.CreateClient();
        using var hidden = await otherClient.GetAsync(path + "/executions");
        await Problem(hidden, HttpStatusCode.NotFound);
        using var missing = await client.GetAsync("/api/workflows/" + Guid.NewGuid() + "/executions");
        await Problem(missing, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("offset=-1")]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=abc")]
    [InlineData("offset=2147483648")]
    public async Task Invalid_pagination_is_rejected(string query)
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, _) = await Create(client);
        using var response = await client.GetAsync(path + "/executions?" + query);
        await Problem(response, HttpStatusCode.BadRequest);
    }
}
