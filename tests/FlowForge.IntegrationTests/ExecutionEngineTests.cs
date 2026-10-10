using System.Text;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;

namespace FlowForge.IntegrationTests;
[Collection("Dispatch")]
public sealed class ExecutionEngineTests(DispatchFixture fixture)
{
    private PostgresExecutionStore Reader => new(fixture.Factory);
    private PostgresExecutionInboxStore Inbox => new(fixture.Factory);
    private async Task<InboxClaim> Claim(ExecutionRequest request) => await Inbox.TryClaimAsync(DispatchStoreTests.Message(request), EngineOptions.Default.Lease);
    private async Task Expire(ExecutionRequest request)
    {
        await using var db = await fixture.Factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        _ = await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {request.ExecutionId} FOR UPDATE").ToArrayAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inbox_messages SET claim_until = clock_timestamp() - interval '1 second' WHERE message_id = {request.MessageId}");
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Checkpoint_recovery_uses_protected_output_and_does_not_repeat_completed_trigger()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var claim = await Claim(request); var store = fixture.EngineStore;
        var checkpoint = (await store.LoadAsync(claim))!;
        var started = await store.BeginNodeAsync(claim, checkpoint.Revision, checkpoint.NextNodeId);
        var output = Json("{\"resume\":42,\"token\":\"ficticio-confidencial\"}");
        var next = new ExecutionPath(checkpoint.Version, request.OwnerUserId).Next(checkpoint.NextNodeId, "next");
        Assert.Equal(CheckpointWriteStatus.Saved, await store.SaveNodeAsync(claim, started.Revision, checkpoint.NextNodeId, NodeResult.Success(output), next));
        var first = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes.First();
        await Expire(request);
        var recovered = await Claim(request); Assert.True(recovered.Generation > claim.Generation);
        // Nova instância do provider lê o mesmo keyring persistente, como um Worker recriado.
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(recovered));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        var history = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status); Assert.Equal(first, history.Nodes.First());
        Assert.Equal(ExecutionPayload.Summarize(output), history.Nodes.Last().Input);
        Assert.Single(history.Logs); Assert.All(history.Nodes, n => Assert.Equal(1, n.AttemptCount));
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var row = await db.WorkflowExecutions.SingleAsync(); var log = await db.ExecutionLogs.SingleAsync();
        Assert.Equal(output.GetRawText(), fixture.Protection.Unprotect(row.ExecutionContextProtected!, row.Id).GetRawText());
        Assert.DoesNotContain("ficticio-confidencial", Encoding.UTF8.GetString(row.ExecutionContextProtected!));
        Assert.DoesNotContain("mensagem-ficticia-confidencial", Encoding.UTF8.GetString(log.MessageProtected));
        Assert.DoesNotContain("confidencial", System.Text.Json.JsonSerializer.Serialize(history));
    }

    [Theory]
    [InlineData(NodeType.HttpRequest)] [InlineData(NodeType.Delay)]
    public async Task Unsupported_node_fails_and_remaining_nodes_are_skipped(NodeType type)
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture, type, true);
        Assert.Equal(MessageDisposition.Completed, await fixture.Handler().HandleAsync(DispatchStoreTests.Message(request)));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        var nodes = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes;
        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status); Assert.Equal(ExecutionFailureCode.UnsupportedNode, result.ErrorCode);
        Assert.Equal([NodeExecutionStatus.Succeeded, NodeExecutionStatus.Failed, NodeExecutionStatus.Skipped], nodes.Select(n => n.Status));
        Assert.Equal(0, nodes.Last().AttemptCount); Assert.Null(nodes.Last().StartedAt);
    }

    [Fact]
    public async Task Publishing_and_archiving_after_enqueue_do_not_change_the_pinned_graph()
    {
        await fixture.ResetAsync(); var (workflow, request, execution) = await EngineFixtures.RequestAsync(fixture);
        var revision = workflow.Revision; var v = workflow.CreateDraft(Guid.NewGuid(), Start.AddSeconds(1));
        workflow.ReplaceDraftGraph(v.Nodes.Select(n => n.Type == NodeType.Log
            ? new WorkflowNode(v.Id, n.NodeId, NodeType.Log, new LogConfiguration("Nova")) : n).ToArray(), v.Connections, Start.AddSeconds(2));
        workflow.PublishDraft(Start.AddSeconds(3)); workflow.Archive(Start.AddSeconds(4));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(workflow, revision);
        await fixture.Handler().HandleAsync(DispatchStoreTests.Message(request));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(execution.WorkflowVersionId, result.WorkflowVersionId); Assert.NotEqual(workflow.CurrentPublishedVersionId, result.WorkflowVersionId);
        Assert.Equal(WorkflowExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Encoding.UTF8.GetByteCount("mensagem-ficticia-confidencial"), Assert.Single((await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Logs).MessageByteLength);
    }

    [Fact]
    public async Task Cancellation_before_claim_never_starts_a_node_or_execution()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var first = await Reader.RequestCancellationAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(first, await Reader.RequestCancellationAsync(request.ExecutionId, request.OwnerUserId));
        await fixture.Handler().HandleAsync(DispatchStoreTests.Message(request));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status); Assert.Null(result.StartedAt); Assert.Null(result.ErrorCode);
        Assert.All((await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes, n => { Assert.Equal(NodeExecutionStatus.Skipped, n.Status); Assert.Equal(0, n.AttemptCount); });
    }

    [Fact]
    public async Task Cancellation_between_nodes_preserves_completed_checkpoint_and_skips_remaining_nodes()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var claim = await Claim(request); var store = fixture.EngineStore;
        var checkpoint = (await store.LoadAsync(claim))!; var start = await store.BeginNodeAsync(claim, checkpoint.Revision, checkpoint.NextNodeId);
        var next = new ExecutionPath(checkpoint.Version, request.OwnerUserId).Next(checkpoint.NextNodeId, "next");
        await store.SaveNodeAsync(claim, start.Revision, checkpoint.NextNodeId, NodeResult.Success(checkpoint.Input), next);
        await Reader.RequestCancellationAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(claim));
        var history = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal([NodeExecutionStatus.Succeeded, NodeExecutionStatus.Skipped], history.Nodes.Select(n => n.Status));
        Assert.Empty(history.Logs);
    }

    [Fact]
    public async Task Cancellation_during_a_cooperative_node_marks_it_cancelled_and_skips_the_tail()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture, tail: true);
        var gate = new GateExecutor(); var options = new EngineOptions(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        var handler = new ExecutionMessageHandler(Inbox, fixture.Engine([new TriggerNodeExecutor(), gate], options));
        var task = handler.HandleAsync(DispatchStoreTests.Message(request));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Reader.RequestCancellationAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(MessageDisposition.Completed, await task.WaitAsync(TimeSpan.FromSeconds(10)));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal([NodeExecutionStatus.Succeeded, NodeExecutionStatus.Cancelled, NodeExecutionStatus.Skipped],
            (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes.Select(n => n.Status));
    }

    [Fact]
    public async Task Expired_generation_cannot_renew_begin_or_save_after_reclaim()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var old = await Claim(request); var store = fixture.EngineStore; var cp = (await store.LoadAsync(old))!;
        var started = await store.BeginNodeAsync(old, cp.Revision, cp.NextNodeId); await Expire(request);
        var current = await Claim(request); Assert.True(current.Generation > old.Generation);
        Assert.Equal(LeaseStatus.Lost, await store.RenewAsync(old, EngineOptions.Default.Lease));
        Assert.Equal(NodeStartStatus.Lost, (await store.BeginNodeAsync(old, started.Revision, cp.NextNodeId)).Status);
        Assert.Equal(CheckpointWriteStatus.Lost, await store.SaveNodeAsync(old, started.Revision, cp.NextNodeId, NodeResult.Success(cp.Input), null));
        Assert.Null(await store.LoadAsync(old));
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(current));
        Assert.Equal(2, (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes.First().AttemptCount);
    }

    [Fact]
    public async Task Heartbeat_keeps_long_node_owned_beyond_the_original_lease()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var gate = new GateExecutor(); var options = new EngineOptions(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
        var handler = new ExecutionMessageHandler(Inbox, fixture.Engine([new TriggerNodeExecutor(), gate], options));
        var task = handler.HandleAsync(DispatchStoreTests.Message(request));
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Task.Delay(TimeSpan.FromMilliseconds(3300));
            Assert.Equal(InboxClaimStatus.Busy, (await Inbox.TryClaimAsync(DispatchStoreTests.Message(request), options.Lease)).Status);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(MessageDisposition.Completed, await task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Worker_shutdown_does_not_become_user_cancellation_and_safe_node_can_resume()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var gate = new GateExecutor(); using var shutdown = new CancellationTokenSource();
        var handler = new ExecutionMessageHandler(Inbox, fixture.Engine([new TriggerNodeExecutor(), gate]));
        var task = handler.HandleAsync(DispatchStoreTests.Message(request), shutdown.Token);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await shutdown.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var running = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Running, running.Status); Assert.Null(running.CancelRequestedAt);
        Assert.Equal(NodeExecutionStatus.Running, (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes.Last().Status);
        await Expire(request);
        Assert.Equal(MessageDisposition.Completed, await fixture.Handler().HandleAsync(DispatchStoreTests.Message(request)));
        Assert.Equal(2, (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Nodes.Last().AttemptCount);
        Assert.Equal(running.StartedAt, (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!.StartedAt);
    }

    [Fact]
    public async Task Log_checkpoint_commit_failure_rolls_back_log_output_and_completion_together()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var claim = await Claim(request); var store = fixture.EngineStore; var cp = (await store.LoadAsync(claim))!;
        var start = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        var next = new ExecutionPath(cp.Version, request.OwnerUserId).Next(cp.NextNodeId, "next");
        await store.SaveNodeAsync(claim, start.Revision, cp.NextNodeId, NodeResult.Success(cp.Input), next);
        cp = (await store.LoadAsync(claim))!; start = await store.BeginNodeAsync(claim, cp.Revision, cp.NextNodeId);
        await using var setup = await fixture.Factory.CreateDbContextAsync();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_engine_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.completed_at IS NOT NULL THEN RAISE EXCEPTION 'injected failure'; END IF; RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER fail_engine_commit AFTER UPDATE ON inbox_messages
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_engine_commit();
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.SaveNodeAsync(claim, start.Revision, cp.NextNodeId,
                NodeResult.Success(Json("{\"result\":42}"), logMessage: "Mensagem"), null));
            await using var db = await fixture.Factory.CreateDbContextAsync();
            Assert.Empty(await db.ExecutionLogs.ToArrayAsync()); Assert.Null((await db.InboxMessages.SingleAsync()).CompletedAt);
            Assert.Equal(WorkflowExecutionStatus.Running, (await db.WorkflowExecutions.SingleAsync()).Status);
            Assert.Equal(NodeExecutionStatus.Running, (await db.NodeExecutions.SingleAsync(n => n.NodeId == cp.NextNodeId)).Status);
            Assert.Equal("{}", fixture.Protection.Unprotect((await db.WorkflowExecutions.SingleAsync()).ExecutionContextProtected!, request.ExecutionId).GetRawText());
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_engine_commit ON inbox_messages; DROP FUNCTION fail_engine_commit();"); }
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(claim));
        Assert.Single((await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Logs);
    }

    [Fact]
    public async Task Lease_loss_during_execution_stops_old_worker_without_overwriting_recovered_result()
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture);
        var gate = new GateExecutor(); var options = new EngineOptions(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        var handler = new ExecutionMessageHandler(Inbox, fixture.Engine([new TriggerNodeExecutor(), gate], options));
        var task = handler.HandleAsync(DispatchStoreTests.Message(request));
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Expire(request); var current = await Claim(request);
        Assert.Equal(InboxClaimStatus.Acquired, current.Status);
        Assert.Equal(MessageDisposition.Completed, await fixture.Engine().RunAsync(current));
        var recovered = await Reader.GetAsync(request.ExecutionId, request.OwnerUserId);
        Assert.Equal(MessageDisposition.Busy, await task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(recovered, await Reader.GetAsync(request.ExecutionId, request.OwnerUserId));
        Assert.Single((await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!.Logs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Executor_failure_or_timeout_records_error_and_skips_remaining_nodes(bool timeout)
    {
        await fixture.ResetAsync(); var (_, request, _) = await EngineFixtures.RequestAsync(fixture, tail: true);
        INodeExecutor executor = timeout ? new GateExecutor() : new FaultExecutor();
        var options = EngineOptions.Default with { NodeTimeout = TimeSpan.FromMilliseconds(100) };
        var handler = new ExecutionMessageHandler(Inbox, fixture.Engine([new TriggerNodeExecutor(), executor], options));
        Assert.Equal(MessageDisposition.Completed, await handler.HandleAsync(DispatchStoreTests.Message(request)));
        var result = (await Reader.GetAsync(request.ExecutionId, request.OwnerUserId))!;
        var history = (await Reader.HistoryAsync(request.ExecutionId, request.OwnerUserId))!;
        Assert.Equal(WorkflowExecutionStatus.Failed, result.Status);
        Assert.Equal(timeout ? ExecutionFailureCode.NodeTimeout : ExecutionFailureCode.NodeFailed, result.ErrorCode);
        Assert.Equal([NodeExecutionStatus.Succeeded, NodeExecutionStatus.Failed, NodeExecutionStatus.Skipped], history.Nodes.Select(n => n.Status));
        Assert.Empty(history.Logs); Assert.DoesNotContain("sentinel", System.Text.Json.JsonSerializer.Serialize(history));
    }

    private sealed class FaultExecutor : INodeExecutor
    {
        public NodeType Type => NodeType.Log;
        public bool CanReplayAfterInterruption => true;
        public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct) => throw new InvalidOperationException("secret-sentinel");
    }

    private sealed class GateExecutor : INodeExecutor
    {
        public NodeType Type => NodeType.Log;
        public bool CanReplayAfterInterruption => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
        { Started.TrySetResult(); await Release.Task.WaitAsync(ct); return NodeResult.Success(context.Input, logMessage: "Gate"); }
    }
}
