using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace FlowForge.IntegrationTests;

[Collection("Dispatch")]
public sealed class RabbitDispatchTests(DispatchFixture fixture)
{
    private async Task<ExecutionRequest> NewRequestAsync()
    {
        var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        return request;
    }

    private static ExecutionRequestedMessage Message(ExecutionRequest r) => DispatchStoreTests.Message(r);
    private ExecutionMessageHandler Handler() => fixture.Handler();
    private RabbitExecutionConsumer Consumer(IExecutionMessageHandler handler, CaptureLogger logger) =>
        new(fixture.RabbitOptions, handler, logger);
    private OutboxDispatcher Dispatcher(RabbitExecutionPublisher publisher) =>
        new(new PostgresExecutionOutboxStore(fixture.Factory), publisher);

    [Fact]
    public async Task Confirmed_publication_executes_nodes_commits_history_and_acks()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        var logger = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var task = Consumer(Handler(), logger).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(stop, task); }
        var result = (await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.ErrorCode);
        var history = (await new PostgresExecutionStore(fixture.Factory).HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(2, history.Nodes.Count);
        Assert.All(history.Nodes, n => { Assert.Equal(NodeExecutionStatus.Succeeded, n.Status); Assert.Equal(1, n.AttemptCount); });
        Assert.Single(history.Logs);
        Assert.True(result.FinishedAt >= result.StartedAt);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.NotNull((await db.InboxMessages.SingleAsync()).CompletedAt);
        Assert.NotNull((await db.OutboxMessages.SingleAsync()).PublishedAt);
        Assert.Equal(0U, await ReadyCountAsync()); // Após fechar o consumer, nenhum unacked voltou à fila.
    }

    [Fact]
    public async Task Confirm_then_outbox_mark_failure_republishes_same_message_without_repeating_result()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        var outbox = new PostgresExecutionOutboxStore(fixture.Factory);
        var claim = (await outbox.TryClaimAsync(TimeSpan.FromSeconds(30)))!;
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(claim.Message); // Confirmação real; interrompemos antes de MarkPublished.
        var logger = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var task = Consumer(Handler(), logger).RunSessionAsync(stop.Token);
        try
        {
            await UntilAsync(() => Task.FromResult(logger.Completed >= 1));
            var first = await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId);
            await using var db = await fixture.Factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE outbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE id = {request.MessageId}");
            Assert.True(await Dispatcher(publisher).DispatchOneAsync());
            await UntilAsync(() => Task.FromResult(logger.Completed >= 2));
            Assert.Equal(first, await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId));
            Assert.Equal(1, (await db.InboxMessages.SingleAsync()).ClaimAttempts);
            Assert.Equal(2, (await db.OutboxMessages.SingleAsync()).PublishAttempts);
        }
        finally { await StopAsync(stop, task); }
        Assert.Equal(0U, await ReadyCountAsync());
    }

    [Fact]
    public async Task Duplicate_delivery_to_two_consumers_completes_execution_only_once()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(Message(request)); await publisher.PublishAsync(Message(request));
        var one = new CaptureLogger(); var two = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var first = Consumer(Handler(), one).RunSessionAsync(stop.Token);
        var second = Consumer(Handler(), two).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(one.Completed + two.Completed >= 2)); }
        finally { await StopAsync(stop, Task.WhenAll(first, second)); }
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await db.InboxMessages.SingleAsync()).ClaimAttempts);
        Assert.Equal(1, await db.WorkflowExecutions.CountAsync());
        Assert.Equal(2, await db.NodeExecutions.CountAsync());
        Assert.Equal(1, await db.ExecutionLogs.CountAsync());
        Assert.Equal(0U, await ReadyCountAsync());
    }

    [Fact]
    public async Task Broker_outage_preserves_execution_and_outbox_and_same_dispatcher_recovers()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        var dispatcher = Dispatcher(publisher);
        var stopped = await fixture.Rabbit.ExecAsync(["rabbitmqctl", "stop_app"]);
        Assert.Equal(0L, stopped.ExitCode);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => dispatcher.DispatchOneAsync());
            await using var db = await fixture.Factory.CreateDbContextAsync();
            Assert.Null((await db.OutboxMessages.SingleAsync()).PublishedAt);
            Assert.Equal(WorkflowExecutionStatus.Pending, (await db.WorkflowExecutions.SingleAsync()).Status);
        }
        finally
        {
            var started = await fixture.Rabbit.ExecAsync(["rabbitmqctl", "start_app"]);
            Assert.Equal(0L, started.ExitCode);
        }
        await UntilAsync(() => dispatcher.DispatchOneAsync());
        var logger = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var task = Consumer(Handler(), logger).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(stop, task); }
        Assert.Equal(WorkflowExecutionStatus.Succeeded,
            (await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId))!.Status);
    }

    [Fact]
    public async Task Consumer_interrupted_after_received_claim_is_redelivered_and_reclaimed()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(Message(request));
        var blocked = new InterruptedHandler(new PostgresExecutionInboxStore(fixture.Factory));
        using (var stop = new CancellationTokenSource())
        {
            var task = Consumer(blocked, new CaptureLogger()).RunSessionAsync(stop.Token);
            try { Assert.Equal(InboxClaimStatus.Acquired, (await blocked.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(15))).Status); }
            finally { await StopAsync(stop, task); }
        }
        var running = (await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Running, running.Status);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
            Assert.Null((await db.InboxMessages.SingleAsync()).CompletedAt);
        await UntilAsync(async () => await ReadyCountAsync() == 1);
        var logger = new CaptureLogger();
        using var resumed = new CancellationTokenSource();
        var recovered = Consumer(Handler(), logger).RunSessionAsync(resumed.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(resumed, recovered); }
        var failed = (await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(running.StartedAt, failed.StartedAt);
        await using var final = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(2, (await final.InboxMessages.SingleAsync()).ClaimAttempts);
        Assert.Equal(0U, await ReadyCountAsync());
    }

    [Fact]
    public async Task Persistence_error_does_not_ack_delivery_or_expose_exception_text()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(Message(request));
        var logger = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var task = Consumer(new TransientFailureHandler(Handler()), logger).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(stop, task); }
        Assert.Contains(logger.Messages, m => m.Contains("NpgsqlException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret-sentinel", StringComparison.Ordinal));
        Assert.Equal(0U, await ReadyCountAsync());
    }

    [Fact]
    public async Task Invalid_message_is_rejected_without_logging_body_and_next_valid_delivery_proceeds()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var connection = await fixture.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync(ExecutionRabbitTopology.Exchange, ExecutionRabbitTopology.RoutingKey, true,
            new BasicProperties { Persistent = true, ContentType = "application/json", Type = ExecutionRequestedMessage.MessageType },
            Encoding.UTF8.GetBytes("{\"contractVersion\":99,\"secret\":\"secret-sentinel\"}"));
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(Message(request));
        var logger = new CaptureLogger();
        using var stop = new CancellationTokenSource();
        var task = Consumer(Handler(), logger).RunSessionAsync(stop.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(stop, task); }
        Assert.Contains(logger.Messages, m => m.Contains("invalid_message", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("secret-sentinel", StringComparison.Ordinal));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.InboxMessages.CountAsync());
        Assert.Equal(0U, await ReadyCountAsync());
    }

    [Fact]
    public async Task Mandatory_unroutable_publish_is_not_marked_as_sent()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        Assert.True(await Dispatcher(publisher).DispatchOneAsync());
        await using var connection = await fixture.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        Assert.NotNull(await channel.BasicGetAsync(ExecutionRabbitTopology.Queue, autoAck: true));
        await channel.QueueUnbindAsync(ExecutionRabbitTopology.Queue, ExecutionRabbitTopology.Exchange, ExecutionRabbitTopology.RoutingKey);
        var second = await NewRequestAsync();
        await Assert.ThrowsAsync<PublishReturnException>(() => Dispatcher(publisher).DispatchOneAsync());
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Null((await db.OutboxMessages.SingleAsync(o => o.Id == second.MessageId)).PublishedAt);
        Assert.Equal(WorkflowExecutionStatus.Pending, (await db.WorkflowExecutions.SingleAsync(e => e.Id == second.ExecutionId)).Status);
    }

    private async Task<uint> ReadyCountAsync()
    {
        await using var connection = await fixture.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        return await channel.MessageCountAsync(ExecutionRabbitTopology.Queue);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("Condição de despacho não ocorreu em 20 segundos.");
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task task)
    {
        await stop.CancelAsync();
        try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private sealed class InterruptedHandler(IExecutionInboxStore inbox) : IExecutionMessageHandler
    {
        public TaskCompletionSource<InboxClaim> Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default)
        {
            var claim = await inbox.TryClaimAsync(message, TimeSpan.FromMilliseconds(500), ct);
            Claimed.TrySetResult(claim);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return MessageDisposition.Busy;
        }
    }

    private sealed class TransientFailureHandler(IExecutionMessageHandler next) : IExecutionMessageHandler
    {
        private int calls;
        public Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default) =>
            Interlocked.Increment(ref calls) == 1 ? throw new NpgsqlException("secret-sentinel") : next.HandleAsync(message, ct);
    }

    private sealed class CaptureLogger : ILogger<RabbitExecutionConsumer>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        private int completed;
        public int Completed => Volatile.Read(ref completed);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            Messages.Enqueue(text);
            if (text.StartsWith("Despacho concluído.", StringComparison.Ordinal)) Interlocked.Increment(ref completed);
        }
    }

    [Fact]
    public async Task Consumer_interrupted_after_result_commit_before_ack_replays_without_repeating_result()
    {
        await fixture.ResetAsync();
        var request = await NewRequestAsync();
        await using var publisher = new RabbitExecutionPublisher(fixture.RabbitOptions);
        await publisher.PublishAsync(Message(request));
        var blocked = new InterruptedAfterCommitHandler(Handler());
        using (var stop = new CancellationTokenSource())
        {
            var task = Consumer(blocked, new CaptureLogger()).RunSessionAsync(stop.Token);
            try { await blocked.Committed.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { await StopAsync(stop, task); }
        }
        var first = await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, first!.Status);
        await UntilAsync(async () => await ReadyCountAsync() == 1);
        var logger = new CaptureLogger();
        using var replayStop = new CancellationTokenSource();
        var replayTask = Consumer(Handler(), logger).RunSessionAsync(replayStop.Token);
        try { await UntilAsync(() => Task.FromResult(logger.Completed >= 1)); }
        finally { await StopAsync(replayStop, replayTask); }
        Assert.Equal(first, await new PostgresExecutionStore(fixture.Factory).GetAsync(request.ExecutionId, request.OwnerUserId));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(1, (await db.InboxMessages.SingleAsync()).ClaimAttempts);
        Assert.Equal(0U, await ReadyCountAsync());
    }

    private sealed class InterruptedAfterCommitHandler(IExecutionMessageHandler next) : IExecutionMessageHandler
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default)
        {
            var result = await next.HandleAsync(message, ct);
            if (result != MessageDisposition.Completed) return result;
            Committed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return result;
        }
    }
}
