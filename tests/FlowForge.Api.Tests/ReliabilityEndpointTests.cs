
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;
[Collection("Workflow API")]
public sealed class ReliabilityEndpointTests(WorkflowApiFixture fixture)
{
    private static async Task<(string Location, JsonNode Execution)> Request(HttpClient client)
    {
        var (path, workflow) = await Create(client); var trigger = Guid.NewGuid(); var http = Guid.NewGuid();
        using var graph = await client.PutAsJsonAsync(path + "/draft", new {
            expectedRevision = Revision(workflow), nodes = new object[] {
                new { nodeId = trigger, configuration = new { type = "webhookTrigger" } },
                new { nodeId = http, configuration = new { type = "httpRequest", url = "https://blocked.flowforge.test", method = "get", maxAttempts = 3 } }
            }, connections = new[] { new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = http, sourcePort = "next" } }
        });
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var response = await client.PostAsync(path + "/executions", null); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (response.Headers.Location!.ToString(), await Body(response));
    }
    [Fact]
    public async Task Redrive_and_history_are_private_idempotent_and_never_expose_operational_fields()
    {
        var owner = Guid.NewGuid(); await using var api = fixture.CreateApi(owner); using var client = api.CreateClient();
        var (location, execution) = await Request(client);
        Assert.True(execution["deadlineAt"]!.GetValue<DateTimeOffset>() > execution["createdAt"]!.GetValue<DateTimeOffset>().AddHours(47));
        using var dispatchResponse = await client.GetAsync(location + "/dispatches"); var dispatches = JsonNode.Parse(await dispatchResponse.Content.ReadAsStringAsync())!.AsArray();
        Assert.Single(dispatches); var message = dispatches[0]!["messageId"]!.ToString();
        var command = new { messageId = message, expectedPublishAttempts = 0 };
        using var first = await client.PostAsJsonAsync(location + "/redrive", command); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var duplicate = await client.PostAsJsonAsync(location + "/redrive", command);
        Assert.Equal((await Body(first)).ToJsonString(), (await Body(duplicate)).ToJsonString());
        using var audit = await client.GetAsync(location + "/redrives"); Assert.Single(JsonNode.Parse(await audit.Content.ReadAsStringAsync())!.AsArray());
        await using var other = fixture.CreateApi(); using var otherClient = other.CreateClient();
        foreach (var suffix in new[] { "/attempts", "/dispatches", "/redrives", "/replays" }) {
            using var forbidden = await otherClient.GetAsync(location + suffix); await Problem(forbidden, HttpStatusCode.NotFound);
        }
        using var foreignRedrive = await otherClient.PostAsJsonAsync(location + "/redrive", command); await Problem(foreignRedrive, HttpStatusCode.NotFound);
        foreach (var word in new[] { "claimToken", "Protected", "keyDigest" }) Assert.DoesNotContain(word, dispatches.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        using var invalid = await client.PostAsJsonAsync(location + "/redrive", new { messageId = Guid.NewGuid(), expectedPublishAttempts = -1 });
        await Problem(invalid, HttpStatusCode.BadRequest);
    }
    [Fact]
    public async Task Terminal_replay_requires_key_and_acknowledgement_and_returns_one_new_execution()
    {
        var owner = Guid.NewGuid(); await using var api = fixture.CreateApi(owner); using var client = api.CreateClient();
        var (location, execution) = await Request(client);
        var factory = api.Services.GetRequiredService<IDbContextFactory<FlowForgeDbContext>>(); await using var db = await factory.CreateDbContextAsync();
        var id = Guid.Parse(execution["id"]!.ToString());
        var message = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM outbox_messages WHERE execution_id = {id} AND dispatch_sequence = 0").SingleAsync();
        var handler = new ExecutionMessageHandler(new PostgresExecutionInboxStore(factory), new SequentialExecutionEngine(
            new PostgresExecutionEngineStore(factory, api.Services.GetRequiredService<ExecutionContextProtection>()), [new TriggerNodeExecutor(), new LogNodeExecutor()], EngineOptions.Default));
        await handler.HandleAsync(new(1, message, id, Guid.Parse(execution["correlationId"]!.ToString())));
        using var noKey = await client.PostAsJsonAsync(location + "/replay", new { acknowledgeExternalEffects = true }); await Problem(noKey, HttpStatusCode.BadRequest);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "replay-ficticio");
        using var noAck = await client.PostAsJsonAsync(location + "/replay", new { acknowledgeExternalEffects = false }); await Problem(noAck, HttpStatusCode.Conflict);
        using var created = await client.PostAsJsonAsync(location + "/replay", new { acknowledgeExternalEffects = true });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode); var next = await Body(created);
        Assert.NotEqual(execution["id"]!.ToString(), next["id"]!.ToString()); Assert.Equal(execution["workflowVersionId"]!.ToString(), next["workflowVersionId"]!.ToString());
        using var repeat = await client.PostAsJsonAsync(location + "/replay", new { acknowledgeExternalEffects = true });
        Assert.Equal(next.ToJsonString(), (await Body(repeat)).ToJsonString());
        using var audits = await client.GetAsync(location + "/replays"); var text = await audits.Content.ReadAsStringAsync();
        Assert.Single(JsonNode.Parse(text)!.AsArray()); Assert.DoesNotContain("replay-ficticio", text);
        using var attemptsResponse = await client.GetAsync(location + "/attempts"); var attempts = JsonNode.Parse(await attemptsResponse.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(2, attempts.Count); Assert.Equal("succeeded", attempts[0]!["status"]!.ToString()); Assert.Equal("failed", attempts[1]!["status"]!.ToString());
        using var old = await client.GetAsync(location); Assert.Equal("failed", (await Body(old))["status"]!.ToString());
    }
    [Theory] [InlineData("post", 3, false, HttpStatusCode.BadRequest)] [InlineData("post", 3, true, HttpStatusCode.OK)]
    [InlineData("get", 6, false, HttpStatusCode.BadRequest)]
    public async Task HTTP_contract_requires_bounded_retry_and_declared_idempotency(string method, int attempts, bool support, HttpStatusCode expected)
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient(); var (path, workflow) = await Create(client);
        using var graph = await client.PutAsJsonAsync(path + "/draft", new {
            expectedRevision = Revision(workflow), nodes = new[] { new { nodeId = Guid.NewGuid(), configuration =
                new { type = "httpRequest", url = "https://example.com", method, maxAttempts = attempts, supportsIdempotency = support } } },
            connections = Array.Empty<object>()
        });
        Assert.Equal(expected, graph.StatusCode);
    }
}
