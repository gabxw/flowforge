
using System.Text;
using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class RecoveryAndDeadLetterTests(DispatchFixture fixture)
{
    private PostgresExecutionStore Reader => new(fixture.Factory);
    private OutboxDispatcher Dispatcher(RabbitExecutionPublisher publisher) => new(new PostgresExecutionOutboxStore(fixture.Factory), publisher);
    private RabbitExecutionConsumer Consumer(IExecutionMessageHandler handler) => new(fixture.RabbitOptions, handler, NullLogger<RabbitExecutionConsumer>.Instance);
    private async Task Until(Func<Task<bool>> condition) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!await condition()) await Task.Delay(50, timeout.Token);
    }
    private static async Task Stop(CancellationTokenSource ct, Task consumer) {
        await ct.CancelAsync(); try { await consumer.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
    }
    [Fact]
    public async Task Recovery_republishes_same_message_after_busy_duplicate_was_acked_and_preserves_completed_checkpoint()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var message = DispatchStoreTests.Message(request);
        var outbox = new PostgresExecutionOutboxStore(fixture.Factory);
        Assert.True(await outbox.MarkPublishedAsync((await outbox.TryClaimAsync(EngineOptions.Default.Lease))!));
        var inbox = new PostgresExecutionInboxStore(fixture.Factory); var claim = await inbox.TryClaimAsync(message, EngineOptions.Default.Lease);
        var store = fixture.EngineStore; var cp = (await store.LoadAsync(claim))!; var start = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        await store.SaveNodeAsync(claim, start.Revision, cp.NextNodeId, NodeResult.Success(cp.Input),
            new ExecutionPath(cp.Version, request.OwnerUserId).Next(cp.NextNodeId, "next"));
        var before = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes[0];
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {message.MessageId}");
        var recovery = new PostgresExecutionRecoveryStore(fixture.Factory); Assert.True(await recovery.RecoverOneAsync());
        Assert.False(await recovery.RecoverOneAsync()); Assert.Equal(LeaseStatus.Lost, await store.RenewAsync(claim, EngineOptions.Default.Lease));
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions); Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        using var stop = new CancellationTokenSource(); var consumer = Consumer(fixture.Handler()).RunSessionAsync(stop.Token);
        try { await Until(async () => (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))?.Status == WorkflowExecutionStatus.Succeeded); }
        finally { await Stop(stop, consumer); }
        var history = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(before, history.Nodes[0]); Assert.Single(history.Logs); Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, (await db.InboxMessages.AsNoTracking().SingleAsync()).ClaimAttempts);
    }
    [Fact]
    public async Task Recovery_republishes_a_message_never_claimed_and_stops_after_bounded_publications()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var outbox = new PostgresExecutionOutboxStore(fixture.Factory); var recovery = new PostgresExecutionRecoveryStore(fixture.Factory);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        // Simula a mensagem que foi para a DLQ antes de qualquer Worker criar a inbox.
        async Task PublishAndAge(bool age)
        {
            Assert.True(await outbox.MarkPublishedAsync((await outbox.TryClaimAsync(EngineOptions.Default.Lease))!));
            if (age) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET created_at = created_at - interval '10 minutes', published_at = clock_timestamp() - interval '6 minutes', recovery_not_before = NULL WHERE id = {request.MessageId}");
        }
        await PublishAndAge(age: false);
        Assert.False(await recovery.RecoverOneAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET created_at = created_at - interval '10 minutes', published_at = clock_timestamp() - interval '6 minutes', recovery_not_before = NULL WHERE id = {request.MessageId}");
        Assert.True(await recovery.RecoverOneAsync());
        var returned = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Null(returned.PublishedAt); Assert.Equal(1, returned.PublishAttempts);
        for (var publication = 2; publication < 5; publication++) { await PublishAndAge(age: true); Assert.True(await recovery.RecoverOneAsync()); }
        await PublishAndAge(age: true);
        Assert.False(await recovery.RecoverOneAsync());
        var kept = await db.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.NotNull(kept.PublishedAt); Assert.Equal(5, kept.PublishAttempts); Assert.Empty(await db.InboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(WorkflowExecutionStatus.Pending, (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!.Status);
    }
    [Fact]
    public async Task Redrive_is_owner_scoped_idempotent_and_cannot_take_a_live_lease()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var recovery = new PostgresExecutionRecoveryStore(fixture.Factory); var body = new RedriveRequest(request.MessageId, 0);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => recovery.RedriveAsync(request.ExecutionId, Guid.NewGuid(), body));
        var audit = await recovery.RedriveAsync(request.ExecutionId, request.OwnerUserId, body);
        Assert.Equal(audit, await recovery.RedriveAsync(request.ExecutionId, request.OwnerUserId, body));
        Assert.Single((await recovery.RedrivesAsync(request.ExecutionId, request.OwnerUserId))!);
        var outbox = new PostgresExecutionOutboxStore(fixture.Factory); var publish = (await outbox.TryClaimAsync(EngineOptions.Default.Lease))!;
        Assert.True(await outbox.MarkPublishedAsync(publish));
        await new PostgresExecutionInboxStore(fixture.Factory).TryClaimAsync(DispatchStoreTests.Message(request), EngineOptions.Default.Lease);
        await Assert.ThrowsAsync<WorkflowStateConflictException>(() => recovery.RedriveAsync(request.ExecutionId, request.OwnerUserId, new(request.MessageId, 1)));
        await Assert.ThrowsAsync<WorkflowStateConflictException>(() => recovery.RedriveAsync(request.ExecutionId, request.OwnerUserId, new(Guid.NewGuid(), 1)));
    }
    [Fact]
    public async Task Invalid_body_is_replaced_by_sanitized_dead_letter_and_a_business_failure_is_not_dead_lettered()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture, FlowForge.Domain.Workflows.NodeType.HttpRequest);
        await using var connection = await fixture.ConnectAsync(); await using var channel = await connection.CreateChannelAsync();
        await ExecutionRabbitTopology.DeclareAsync(channel);
        await channel.BasicPublishAsync(ExecutionRabbitTopology.Exchange, ExecutionRabbitTopology.RoutingKey, true,
            new BasicProperties { ContentType = "application/json", Type = "unknown", Persistent = true },
            Encoding.UTF8.GetBytes("""{"secret":"secret-sentinel","input":"private-payload"}"""));
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions); Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        using var stop = new CancellationTokenSource(); var consume = Consumer(fixture.Handler()).RunSessionAsync(stop.Token);
        try { await Until(async () => (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))?.Status == WorkflowExecutionStatus.Failed); }
        finally { await Stop(stop, consume); }
        var dead = await channel.BasicGetAsync(ExecutionRabbitTopology.DeadLetterQueue, autoAck: true);
        Assert.NotNull(dead); var text = Encoding.UTF8.GetString(dead.Body.Span); Assert.DoesNotContain("secret", text); Assert.DoesNotContain("private-payload", text);
        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(DeliveryFailureCode.InvalidContract, envelope.Reason); Assert.Null(envelope.Message);
        Assert.Null(await channel.BasicGetAsync(ExecutionRabbitTopology.DeadLetterQueue, autoAck: true));
        Assert.Equal(ExecutionFailureCode.UnsupportedNode, (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!.ErrorCode);
        Assert.Equal("flowforge.executions.v1", ExecutionRabbitTopology.Queue); Assert.Equal("execution.requested.v1", ExecutionRabbitTopology.RoutingKey);
    }
    [Fact]
    public async Task Delivery_failure_count_survives_consumer_recreation_and_exhausts_at_five()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions); Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        var interrupted = new FailThenPause(); using (var stop = new CancellationTokenSource()) {
            var first = Consumer(interrupted).RunSessionAsync(stop.Token);
            try { await interrupted.Paused.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { await Stop(stop, first); }
        }
        using var restartedStop = new CancellationTokenSource(); var restarted = Consumer(new AlwaysFail()).RunSessionAsync(restartedStop.Token);
        await using var connection = await fixture.ConnectAsync(); await using var channel = await connection.CreateChannelAsync();
        BasicGetResult? dead = null;
        try { await Until(async () => { dead = await channel.BasicGetAsync(ExecutionRabbitTopology.DeadLetterQueue, true); return dead is not null; }); }
        finally { await Stop(restartedStop, restarted); }
        var payload = Encoding.UTF8.GetString(dead!.Body.Span); Assert.DoesNotContain("secret-sentinel", payload);
        var envelope = JsonSerializer.Deserialize<DeadLetterEnvelope>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(5, envelope.Failures); Assert.Equal(DeliveryFailureCode.ProcessingExhausted, envelope.Reason);
        Assert.Equal(request.MessageId, envelope.Message!.MessageId);
    }
    [Fact]
    public async Task DLQ_transfer_without_route_keeps_original_delivery_until_a_confirmed_transfer_is_possible()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions); Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        await using var connection = await fixture.ConnectAsync(); await using var channel = await connection.CreateChannelAsync();
        var handler = new GatedInvalid(); using var stop = new CancellationTokenSource(); var session = Consumer(handler).RunSessionAsync(stop.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await channel.QueueUnbindAsync(ExecutionRabbitTopology.DeadLetterQueue, ExecutionRabbitTopology.DeadLetterExchange, ExecutionRabbitTopology.DeadLetterRoutingKey);
        handler.Release.TrySetResult();
        try { await session.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await Stop(stop, session); }
        Assert.Null(await channel.BasicGetAsync(ExecutionRabbitTopology.DeadLetterQueue, true));
        var original = await channel.BasicGetAsync(ExecutionRabbitTopology.Queue, autoAck: false); Assert.NotNull(original);
        await channel.BasicNackAsync(original.DeliveryTag, false, true);
        await channel.QueueBindAsync(ExecutionRabbitTopology.DeadLetterQueue, ExecutionRabbitTopology.DeadLetterExchange, ExecutionRabbitTopology.DeadLetterRoutingKey);
    }

    [Fact]
    public async Task Deadline_wakes_long_delay_and_finishes_without_a_new_attempt()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture, FlowForge.Domain.Workflows.NodeType.Delay, true);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_executions SET deadline_at = clock_timestamp() + interval '1 minute' WHERE id = {request.ExecutionId}");
        var handler = new ExecutionMessageHandler(new PostgresExecutionInboxStore(fixture.Factory),
            fixture.Engine([new TriggerNodeExecutor(), new DelayNodeExecutor(), new LogNodeExecutor()]));
        await handler.HandleAsync(DispatchStoreTests.Message(request));
        var snapshot = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(snapshot.DeadlineAt, snapshot.ResumeAt);
        // Vencimento controlado apenas no banco descartável, sem esperar um minuto real.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_executions SET deadline_at = started_at + interval '1 microsecond', resume_at = started_at + interval '1 microsecond' WHERE id = {request.ExecutionId}");
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(o => o.ExecutionId == request.ExecutionId && o.DispatchSequence == 1);
        await handler.HandleAsync(new(1, row.Id, row.ExecutionId, row.CorrelationId));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(ExecutionFailureCode.ExecutionDeadlineExceeded, result.ErrorCode); Assert.Null(result.ResumeAt);
        var history = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(1, history.Nodes[1].AttemptCount); Assert.Equal(NodeExecutionStatus.Failed, history.Nodes[1].Status);
        Assert.Equal(NodeExecutionStatus.Skipped, history.Nodes[2].Status);
    }
    [Fact]
    public async Task Repeated_recovery_claims_are_bounded_before_loading_a_poison_checkpoint()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var inbox = new PostgresExecutionInboxStore(fixture.Factory);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        for (var i = 1; i <= ExecutionLimits.MaximumDeliveryClaims; i++) {
            Assert.Equal(InboxClaimStatus.Acquired, (await inbox.TryClaimAsync(DispatchStoreTests.Message(request), EngineOptions.Default.Lease)).Status);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {request.MessageId}");
        }
        Assert.Equal(InboxClaimStatus.Invalid, (await inbox.TryClaimAsync(DispatchStoreTests.Message(request), EngineOptions.Default.Lease)).Status);
        Assert.Equal(ExecutionFailureCode.AttemptLimitExceeded, (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!.ErrorCode);
        Assert.Equal(InboxClaimStatus.Completed, (await inbox.TryClaimAsync(DispatchStoreTests.Message(request), EngineOptions.Default.Lease)).Status);
    }

    private sealed class AlwaysFail : IExecutionMessageHandler {
        public Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default) => throw new IOException("secret-sentinel");
    }
    private sealed class FailThenPause : IExecutionMessageHandler {
        private int calls; public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default) {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("secret-sentinel");
            Paused.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return MessageDisposition.Completed;
        }
    }
    private sealed class GatedInvalid : IExecutionMessageHandler {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default) {
            Started.TrySetResult(); await Release.Task.WaitAsync(ct); return MessageDisposition.Invalid;
        }
    }
}
