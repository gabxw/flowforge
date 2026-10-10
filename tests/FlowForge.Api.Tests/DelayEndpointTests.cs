
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
public sealed class DelayEndpointTests(WorkflowApiFixture fixture)
{
    [Fact]
    public async Task API_exposes_wait_deadline_and_cancel_only_requests_a_durable_wakeup_without_running_the_engine()
    {
        var owner = Guid.NewGuid(); await using var api = fixture.CreateApi(owner); using var client = api.CreateClient();
        var (path, workflow) = await Create(client); var trigger = Guid.NewGuid(); var delay = Guid.NewGuid(); var log = Guid.NewGuid();
        using var graph = await client.PutAsJsonAsync(path + "/draft", new {
            expectedRevision = Revision(workflow),
            nodes = new object[] {
                new { nodeId = trigger, configuration = new { type = "webhookTrigger" } },
                new { nodeId = delay, configuration = new { type = "delay", durationTicks = TimeSpan.FromHours(24).Ticks } },
                new { nodeId = log, configuration = new { type = "log", message = "Não executar" } } },
            connections = new[] {
                new { id = Guid.NewGuid(), sourceNodeId = trigger, targetNodeId = delay, sourcePort = "next" },
                new { id = Guid.NewGuid(), sourceNodeId = delay, targetNodeId = log, sourcePort = "next" } }
        });
        Assert.Equal(HttpStatusCode.OK, graph.StatusCode);
        using var publish = await client.PostAsJsonAsync(path + "/publish", new { expectedRevision = Revision(await Body(graph)) });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var accepted = await client.PostAsync(path + "/executions", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); var first = await Body(accepted);
        Assert.Null(first["resumeAt"]); var id = Guid.Parse(first["id"]!.ToString()); var location = accepted.Headers.Location!.ToString();
        var factory = api.Services.GetRequiredService<IDbContextFactory<FlowForgeDbContext>>();
        var protection = api.Services.GetRequiredService<ExecutionContextProtection>();
        await using var db = await factory.CreateDbContextAsync();
        var original = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM outbox_messages WHERE execution_id = {id} AND dispatch_sequence = 0").SingleAsync();
        var handler = new ExecutionMessageHandler(new PostgresExecutionInboxStore(factory),
            new SequentialExecutionEngine(new PostgresExecutionEngineStore(factory, protection),
                [new TriggerNodeExecutor(), new DelayNodeExecutor(), new LogNodeExecutor()], EngineOptions.Default));
        Assert.Equal(MessageDisposition.Completed, await handler.HandleAsync(new(1, original, id, Guid.Parse(first["correlationId"]!.ToString()))));
        using var waitingResponse = await client.GetAsync(location); var waiting = await Body(waitingResponse);
        Assert.Equal("running", waiting["status"]!.ToString()); Assert.NotNull(waiting["resumeAt"]); Assert.Null(waiting["finishedAt"]);
        var deadline = waiting["resumeAt"]!.GetValue<DateTimeOffset>();
        Assert.True(deadline > DateTimeOffset.UtcNow.AddHours(23));
        using var historyResponse = await client.GetAsync(location + "/nodes"); var history = JsonNode.Parse(await historyResponse.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal("running", history[1]!["status"]!.ToString()); Assert.Equal(1, history[1]!["attemptCount"]!.GetValue<int>());
        Assert.Equal("pending", history[2]!["status"]!.ToString());
        using var cancellation = await client.PostAsync(location + "/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancellation.StatusCode); var requested = await Body(cancellation);
        Assert.Equal("running", requested["status"]!.ToString()); Assert.Equal(deadline, requested["resumeAt"]!.GetValue<DateTimeOffset>());
        var available = await db.Database.SqlQuery<DateTimeOffset>($"SELECT available_at AS \"Value\" FROM outbox_messages WHERE execution_id = {id} AND dispatch_sequence = 1").SingleAsync();
        Assert.True(available < DateTimeOffset.UtcNow.AddSeconds(1));
        var continuation = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM outbox_messages WHERE execution_id = {id} AND dispatch_sequence = 1").SingleAsync();
        Assert.Equal(MessageDisposition.Completed, await handler.HandleAsync(new(1, continuation, id, Guid.Parse(first["correlationId"]!.ToString()))));
        using var resultResponse = await client.GetAsync(location); var result = await Body(resultResponse);
        Assert.Equal("cancelled", result["status"]!.ToString()); Assert.Null(result["resumeAt"]); Assert.NotNull(result["finishedAt"]);
        using var logs = await client.GetAsync(location + "/logs"); Assert.Empty(JsonNode.Parse(await logs.Content.ReadAsStringAsync())!.AsArray());
        await using var other = fixture.CreateApi(); using var otherClient = other.CreateClient();
        using var hidden = await otherClient.GetAsync(location); await Problem(hidden, HttpStatusCode.NotFound);
        Assert.DoesNotContain("Protected", waiting.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}
