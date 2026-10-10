using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Persistence;
using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;
[Collection("Workflow API")]
public sealed class ExecutionEngineEndpointTests(WorkflowApiFixture fixture)
{
    [Fact]
    public async Task Cancellation_is_idempotent_private_and_rejects_a_body()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient();
        var location = await Request(client);
        using var cancelled = await client.PostAsync(location + "/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        var body = await Body(cancelled); Assert.Equal("pending", body["status"]!.ToString());
        Assert.NotNull(body["cancelRequestedAt"]); Assert.Null(body["startedAt"]);
        using var repeated = await client.PostAsync(location + "/cancel", null);
        Assert.Equal(body.ToJsonString(), (await Body(repeated)).ToJsonString());
        using var invalid = await client.PostAsJsonAsync(location + "/cancel", new { reason = "secret-sentinel" });
        Assert.DoesNotContain("sentinel", (await Problem(invalid, HttpStatusCode.BadRequest)).ToJsonString());
        await using var other = fixture.CreateApi(); using var otherClient = other.CreateClient();
        foreach (var path in new[] { "/nodes", "/logs" })
        { using var hidden = await otherClient.GetAsync(location + path); await Problem(hidden, HttpStatusCode.NotFound); }
        using var forbidden = await otherClient.PostAsync(location + "/cancel", null);
        await Problem(forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Completed_history_exposes_metadata_without_context_ciphertext_or_log_message()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient();
        var location = await Request(client);
        using var read = await client.GetAsync(location); var execution = await Body(read);
        var factory = api.Services.GetRequiredService<IDbContextFactory<FlowForgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var id = Guid.Parse(execution["id"]!.ToString());
        var message = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM outbox_messages WHERE execution_id = {id}").SingleAsync();
        var engine = new SequentialExecutionEngine(new PostgresExecutionEngineStore(factory, new ExecutionContextProtection(new EphemeralDataProtectionProvider())),
            [new TriggerNodeExecutor(), new LogNodeExecutor()], EngineOptions.Default);
        var handler = new ExecutionMessageHandler(new PostgresExecutionInboxStore(factory), engine);
        Assert.Equal(MessageDisposition.Completed, await handler.HandleAsync(new(1, message, id, Guid.Parse(execution["correlationId"]!.ToString()))));
        using var nodesResponse = await client.GetAsync(location + "/nodes"); var nodesText = await nodesResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, nodesResponse.StatusCode);
        var nodes = JsonNode.Parse(nodesText)!.AsArray(); Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => { Assert.Equal("succeeded", n!["status"]!.ToString()); Assert.Equal(1, n["attemptCount"]!.GetValue<int>());
            Assert.Equal(2, n["input"]!["byteLength"]!.GetValue<int>()); });
        using var logsResponse = await client.GetAsync(location + "/logs"); var logsText = await logsResponse.Content.ReadAsStringAsync();
        var logs = JsonNode.Parse(logsText)!.AsArray(); Assert.Single(logs);
        Assert.Equal("logRecorded", logs[0]!["eventCode"]!.ToString());
        foreach (var text in new[] { nodesText, logsText })
        { Assert.DoesNotContain("secret-sentinel", text); Assert.DoesNotContain("Protected", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("ownerUserId", text); }
        using var cancelled = await client.PostAsync(location + "/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var final = await Body(cancelled); Assert.Equal("succeeded", final["status"]!.ToString()); Assert.Null(final["cancelRequestedAt"]);
    }

    [Fact]
    public async Task Missing_execution_has_no_history_or_cancel_target()
    {
        await using var api = fixture.CreateApi(); using var client = api.CreateClient();
        var path = "/api/executions/" + Guid.NewGuid();
        foreach (var suffix in new[] { "/nodes", "/logs" })
        { using var missing = await client.GetAsync(path + suffix); await Problem(missing, HttpStatusCode.NotFound); }
        using var cancel = await client.PostAsync(path + "/cancel", null); await Problem(cancel, HttpStatusCode.NotFound);
    }

    private static async Task<string> Request(HttpClient client)
    {
        var (path, workflow) = await Create(client); var trigger = Guid.NewGuid(); var log = Guid.NewGuid();
        using var graph = await client.PutAsJsonAsync(path + "/draft", new { expectedRevision = Revision(workflow),
            nodes = new object[] { new { nodeId = trigger, configuration = new { type = "webhookTrigger" } },
                new { nodeId = log, configuration = new { type = "log", message = "secret-sentinel" } } },
            connections = new[] { new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = log, sourcePort = "next" } } });
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var execution = await client.PostAsync(path + "/executions", null);
        Assert.Equal(HttpStatusCode.Accepted, execution.StatusCode); return execution.Headers.Location!.ToString();
    }
}
