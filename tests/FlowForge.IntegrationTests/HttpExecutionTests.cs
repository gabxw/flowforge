using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FlowForge.Application.Credentials;
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Domain.Credentials;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Http;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;
namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class HttpExecutionTests(DispatchFixture fixture, ControlledHttpsServer server) : IClassFixture<ControlledHttpsServer>
{
    private PostgresExecutionStore Reader => new(fixture.Factory);
    private PostgresCredentialStore Credentials => new(fixture.Factory, fixture.CredentialProtection);
    private HttpRequestNodeExecutor Http(TimeSpan? timeout = null) {
        var options = new HttpNodeOptions([server.Origin], timeout);
        return new(Credentials, new HttpDestinationPolicy(options, (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") })), server.Transport(), options);
    }
    private ExecutionMessageHandler Handler(TimeSpan? timeout = null) => new(new PostgresExecutionInboxStore(fixture.Factory),
        fixture.Engine([new TriggerNodeExecutor(), Http(timeout), new LogNodeExecutor()]));
    private async Task<(Workflow Workflow, CredentialSnapshot Credential, CredentialSecret Secret)> WorkflowAsync(string path)
    {
        var owner = Guid.NewGuid(); var now = WorkflowStoreFixtures.Start;
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, now);
        var metadata = new Credential(Guid.NewGuid(), owner, "Controlled HTTPS", CredentialType.BearerToken, HttpsOrigin.Parse(server.Origin), null, now).Snapshot;
        var secret = new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N"));
        await Credentials.CreateAsync(metadata, secret);
        var workflow = new Workflow(Guid.NewGuid(), owner, "Webhook HTTP Log", now); var version = workflow.CreateDraft(Guid.NewGuid(), now);
        var trigger = Guid.NewGuid(); var http = Guid.NewGuid(); var log = Guid.NewGuid();
        workflow.ReplaceDraftGraph([
            new(version.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
            new(version.Id, http, NodeType.HttpRequest, new HttpRequestConfiguration(new(server.Origin + path), HttpRequestMethod.Post), credential: new(metadata.Id, owner)),
            new(version.Id, log, NodeType.Log, new LogConfiguration("HTTP concluído"))
        ], [new(Guid.NewGuid(), version.Id, trigger, http, "next"), new(Guid.NewGuid(), version.Id, http, log, "next")], now);
        workflow.PublishDraft(now); await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        return (workflow, metadata, secret);
    }
    private async Task<ExecutionRequestedMessage> RequestAsync(Workflow workflow) {
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflow.Id, workflow.OwnerUserId);
        await Reader.RequestAsync(request); return DispatchStoreTests.Message(request);
    }
    [Fact]
    public async Task Webhook_outbox_real_broker_http_and_log_complete_once_with_independent_keyring_readers()
    {
        await fixture.ResetAsync(); var (workflow, credential, secret) = await WorkflowAsync("/ok");
        var hooks = new PostgresWebhookStore(fixture.Factory, fixture.Protection, WebhookAcceptanceOptions.Default);
        var hook = await hooks.CreateAsync(workflow.Id, workflow.OwnerUserId);
        var receipt = await hooks.AcceptAsync(hook.Endpoint.Id, hook.Secret, WebhookPayload.Parse("{\"value\":42}"u8.ToArray()), "controlled-event");
        await using var db = await fixture.Factory.CreateDbContextAsync(); var outbox = await db.OutboxMessages.SingleAsync();
        var message = new ExecutionRequestedMessage(1, outbox.Id, receipt.ExecutionId, receipt.CorrelationId);
        var before = server.Counts.GetValueOrDefault("/ok");
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        Assert.True(await new OutboxDispatcher(new PostgresExecutionOutboxStore(fixture.Factory), publisher).DispatchOneAsync());
        await publisher.PublishAsync(message); // Redelivery duplicada não equivale a outro efeito HTTP.
        var logs = new ConsumerLogs(); using var stop = new CancellationTokenSource();
        var consume = new RabbitExecutionConsumer(fixture.RabbitOptions, Handler(), logs).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(logs.Completed >= 2)); }
        finally { await stop.CancelAsync(); try { await consume.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { } }
        var execution = (await Reader.GetAsync(receipt.ExecutionId, workflow.OwnerUserId))!;
        var history = (await Reader.HistoryAsync(receipt.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Succeeded, execution.Status); Assert.Single(history.Logs); Assert.Equal(before + 1, server.Counts["/ok"]);
        Assert.All(history.Nodes, n => { Assert.Equal(NodeExecutionStatus.Succeeded, n.Status); Assert.Equal(1, n.AttemptCount); });
        Assert.Equal(credential.Revision, history.Nodes[1].CredentialRevisionUsed);
        var row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(); var context = fixture.Protection.Unprotect(row.ExecutionContextProtected!, row.Id);
        Assert.Equal(42, context.GetProperty("body").GetProperty("input").GetProperty("value").GetInt32());
        Assert.DoesNotContain(secret.Value, context.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Value, JsonSerializer.Serialize(history), StringComparison.Ordinal);
        Assert.DoesNotContain(secret.Value, string.Join(" ", logs.Values), StringComparison.Ordinal);
        Assert.NotEmpty(logs.Values); Assert.Equal(1, (await db.InboxMessages.AsNoTracking().SingleAsync()).ClaimAttempts);
        Assert.DoesNotContain(secret.Value, Encoding.UTF8.GetString(row.TriggerInputProtected!), StringComparison.Ordinal);
    }
    [Theory][InlineData("/slow", ExecutionFailureCode.NodeTimeout)][InlineData("/echo", ExecutionFailureCode.HttpResponseSensitive)]
    public async Task Failure_and_timeout_persist_safe_codes_known_revision_and_skip_the_tail(string path, ExecutionFailureCode code)
    {
        await fixture.ResetAsync(); var (workflow, credential, secret) = await WorkflowAsync(path); var message = await RequestAsync(workflow);
        Assert.Equal(MessageDisposition.Completed, await Handler(TimeSpan.FromMilliseconds(200)).HandleAsync(message));
        var result = (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!; var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status); Assert.Equal(code, result.ErrorCode);
        Assert.Equal(credential.Revision, history.Nodes[1].CredentialRevisionUsed); Assert.Empty(history.Logs);
        Assert.Equal(NodeExecutionStatus.Skipped, history.Nodes[2].Status); Assert.Null(history.Nodes[1].Output);
        Assert.DoesNotContain(secret.Value, JsonSerializer.Serialize(history), StringComparison.Ordinal);
    }
    [Fact]
    public async Task Remote_effect_before_checkpoint_loss_is_not_sent_again_or_counted_as_a_new_attempt()
    {
        await fixture.ResetAsync(); var (workflow, _, _) = await WorkflowAsync("/ok"); var message = await RequestAsync(workflow);
        var inbox = new PostgresExecutionInboxStore(fixture.Factory); var claim = await inbox.TryClaimAsync(message, EngineOptions.Default.Lease);
        var store = fixture.EngineStore; var checkpoint = (await store.LoadAsync(claim))!;
        var start = await store.BeginNodeAsync(claim, checkpoint.Revision, checkpoint.NextNodeId);
        var next = new ExecutionPath(checkpoint.Version, workflow.OwnerUserId).Next(checkpoint.NextNodeId, "next");
        await store.SaveNodeAsync(claim, start.Revision, checkpoint.NextNodeId, NodeResult.Success(checkpoint.Input), next);
        checkpoint = (await store.LoadAsync(claim))!;
        _ = await store.BeginNodeAsync(claim, checkpoint.Revision, checkpoint.NextNodeId);
        var before = server.Counts.GetValueOrDefault("/ok"); var definition = checkpoint.Version.Nodes.Single(n => n.NodeId == checkpoint.NextNodeId);
        Assert.Null((await Http().ExecuteAsync(new(message.ExecutionId, message.CorrelationId, definition, checkpoint.Input, workflow.OwnerUserId), default)).ErrorCode);
        // O destino confirmou seu efeito. Simulamos a queda antes de SaveNode; não existe rollback remoto.
        await using (var db = await fixture.Factory.CreateDbContextAsync()) {
            await using var tx = await db.Database.BeginTransactionAsync();
            _ = await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {message.ExecutionId} FOR UPDATE").ToArrayAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {message.MessageId}"); await tx.CommitAsync();
        }
        Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        Assert.Equal(before + 1, server.Counts["/ok"]); var history = (await Reader.HistoryAsync(message.ExecutionId, workflow.OwnerUserId))!;
        Assert.Equal(ExecutionFailureCode.InterruptedNode, history.Nodes[1].ErrorCode); Assert.Equal(1, history.Nodes[1].AttemptCount);
        Assert.Null(history.Nodes[1].CredentialRevisionUsed); // Não inventar metadados da tentativa cujo checkpoint se perdeu.
        Assert.Empty(history.Logs); Assert.Equal(NodeExecutionStatus.Skipped, history.Nodes[2].Status);
    }
    [Fact]
    public async Task Rotation_does_not_change_an_inflight_request_and_the_next_execution_uses_the_new_revision()
    {
        await fixture.ResetAsync(); var path = "/gate/" + Guid.NewGuid().ToString("N");
        var gate = new ControlledHttpsServer.Gate(); Assert.True(server.Gates.TryAdd(path, gate));
        var (workflow, credential, original) = await WorkflowAsync(path); var first = await RequestAsync(workflow);
        var running = Handler().HandleAsync(first);
        var next = new CredentialSecret("ficticio-" + Guid.NewGuid().ToString("N"));
        try {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Credentials.RotateAsync(credential.Id, credential.OwnerUserId, 1, next, credential.UpdatedAt.AddSeconds(1));
            Assert.Equal("Bearer " + original.Value, server.Observations.Last().Authorization);
        } finally { gate.Release.TrySetResult(); }
        Assert.Equal(MessageDisposition.Completed, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, (await Reader.HistoryAsync(first.ExecutionId, workflow.OwnerUserId))!.Nodes[1].CredentialRevisionUsed);
        var second = await RequestAsync(workflow); Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(second));
        Assert.Equal("Bearer " + next.Value, server.Observations.Last().Authorization);
        Assert.Equal(2, (await Reader.HistoryAsync(second.ExecutionId, workflow.OwnerUserId))!.Nodes[1].CredentialRevisionUsed);
    }

    [Fact]
    public async Task Revocation_after_publication_prevents_a_pending_execution_from_sending_credentials()
    {
        await fixture.ResetAsync(); var (workflow, credential, _) = await WorkflowAsync("/ok"); var message = await RequestAsync(workflow);
        await Credentials.RevokeAsync(credential.Id, credential.OwnerUserId, 1, credential.UpdatedAt.AddSeconds(1));
        var before = server.Counts.GetValueOrDefault("/ok"); Assert.Equal(MessageDisposition.Completed, await Handler().HandleAsync(message));
        Assert.Equal(before, server.Counts.GetValueOrDefault("/ok")); Assert.Equal(ExecutionFailureCode.CredentialUnavailable, (await Reader.GetAsync(message.ExecutionId, workflow.OwnerUserId))!.ErrorCode);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition) {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(50, limit.Token);
    }
    private sealed class ConsumerLogs : ILogger<RabbitExecutionConsumer> {
        private int completed; public int Completed => Volatile.Read(ref completed); public ConcurrentQueue<string> Values { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format) {
            Values.Enqueue(format(state, exception)); if (format(state, exception).StartsWith("Despacho concluído", StringComparison.Ordinal)) Interlocked.Increment(ref completed);
        }
    }
}
