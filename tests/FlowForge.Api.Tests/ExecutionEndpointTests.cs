using System.Net;
using System.Net.Http.Json;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;

[Collection("Workflow API")]
public sealed class ExecutionEndpointTests(WorkflowApiFixture fixture)
{
    [Fact]
    public async Task Published_execution_returns_202_location_pending_state_and_pinned_version_without_RabbitMQ()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var graph = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        var published = await Body(publish);
        using var requested = await client.PostAsync(path + "/executions", null);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var execution = await Body(requested);
        Assert.Equal("pending", execution["status"]!.GetValue<string>());
        Assert.Equal(published["currentPublishedVersionId"]!.ToString(), execution["workflowVersionId"]!.ToString());
        Assert.False(execution.ContainsKey("ownerUserId"));
        Assert.Null(execution["errorCode"]); Assert.Null(execution["startedAt"]);
        using var read = await client.GetAsync(requested.Headers.Location);
        Assert.Equal(execution.ToJsonString(), (await Body(read)).ToJsonString());
        using var second = await client.PostAsync(path + "/executions", null);
        Assert.NotEqual(execution["id"]!.ToString(), (await Body(second))["id"]!.ToString());
    }

    [Fact]
    public async Task Draft_and_archived_workflow_reject_execution()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var draft = await client.PostAsync(path + "/executions", null);
        await Problem(draft, HttpStatusCode.Conflict);
        using var graph = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        using var archive = await client.PostAsJsonAsync(path + "/archive", new { expectedRevision = Revision(await Body(publish)) });
        using var archived = await client.PostAsync(path + "/executions", null);
        await Problem(archived, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Another_owner_cannot_request_or_read_execution()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, workflow) = await Create(client);
        using var graph = await client.PutAsJsonAsync(path + "/draft", Graph(Revision(workflow)));
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        using var request = await client.PostAsync(path + "/executions", null);
        await using var other = fixture.CreateApi();
        using var otherClient = other.CreateClient();
        using var rejected = await otherClient.PostAsync(path + "/executions", null);
        await Problem(rejected, HttpStatusCode.NotFound);
        using var hidden = await otherClient.GetAsync(request.Headers.Location);
        await Problem(hidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Body_cannot_override_execution_owner_version_or_payload()
    {
        await using var api = fixture.CreateApi();
        using var client = api.CreateClient();
        var (path, _) = await Create(client);
        using var request = await client.PostAsJsonAsync(path + "/executions", new { ownerUserId = Guid.NewGuid(), payload = new { secret = "sentinel" } });
        var problem = await Problem(request, HttpStatusCode.BadRequest);
        Assert.DoesNotContain("sentinel", problem.ToJsonString());
        using var missing = await client.GetAsync("/api/executions/" + Guid.NewGuid());
        await Problem(missing, HttpStatusCode.NotFound);
    }
}
