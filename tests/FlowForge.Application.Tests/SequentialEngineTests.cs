using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Application.Tests;
public sealed class SequentialEngineTests
{
    [Fact]
    public async Task Engine_traverses_graph_and_propagates_real_output_to_the_next_node()
    {
        var store = new RecordingStore(); var seen = new List<string>();
        var trigger = new Executor(NodeType.WebhookTrigger, (c, _) => Task.FromResult(NodeResult.Success(Json("{\"result\":42}"))));
        var log = new Executor(NodeType.Log, (c, _) => { seen.Add(c.Input.GetRawText()); return Task.FromResult(NodeResult.Success(c.Input)); });
        Assert.Equal(MessageDisposition.Completed, await Engine(store, [trigger, log]).RunAsync(store.Claim));
        Assert.Equal(store.Version.Nodes.Reverse().Select(n => n.NodeId), store.Started);
        Assert.Equal(["{\"result\":42}"], seen);
        Assert.All(store.Results, r => Assert.Null(r.ErrorCode));
    }

    [Fact]
    public async Task Unsupported_executor_fails_instead_of_succeeding_or_starting_a_following_node()
    {
        var store = new RecordingStore();
        await Engine(store, [new TriggerNodeExecutor()]).RunAsync(store.Claim);
        Assert.Equal(ExecutionFailureCode.UnsupportedNode, store.Results.Last().ErrorCode);
    }

    [Fact]
    public async Task Executor_exception_is_reduced_to_a_code_without_exposing_its_message()
    {
        var store = new RecordingStore();
        var bad = new Executor(NodeType.Log, (_, _) => throw new InvalidOperationException("secret-sentinel"));
        await Engine(store, [new TriggerNodeExecutor(), bad]).RunAsync(store.Claim);
        Assert.Equal(ExecutionFailureCode.NodeFailed, store.Results.Last().ErrorCode);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(store.Results));
    }

    [Fact]
    public async Task Cooperative_node_timeout_is_recorded_as_a_failure()
    {
        var store = new RecordingStore();
        var waiting = new Executor(NodeType.Log, async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return NodeResult.Success(Json("{}")); });
        await Engine(store, [new TriggerNodeExecutor(), waiting], TimeSpan.FromMilliseconds(100)).RunAsync(store.Claim);
        Assert.Equal(ExecutionFailureCode.NodeTimeout, store.Results.Last().ErrorCode);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Invalid_output_or_port_fails_before_persisting_a_success(bool oversized)
    {
        var store = new RecordingStore();
        var bad = new Executor(NodeType.Log, (c, _) => Task.FromResult(oversized
            ? NodeResult.Success(Json(JsonSerializer.Serialize(new string('x', 65536))))
            : NodeResult.Success(c.Input, "unknown")));
        await Engine(store, [new TriggerNodeExecutor(), bad]).RunAsync(store.Claim);
        Assert.Equal(oversized ? ExecutionFailureCode.ContextLimitExceeded : ExecutionFailureCode.InvalidExecutorResult,
            store.Results.Last().ErrorCode);
        Assert.Null(store.Results.Last().Output);
    }

    [Fact]
    public async Task Lost_checkpoint_does_not_execute_any_node()
    {
        var store = new RecordingStore { Lost = true };
        Assert.Equal(MessageDisposition.Busy, await Engine(store, [new TriggerNodeExecutor(), new LogNodeExecutor()]).RunAsync(store.Claim));
        Assert.Empty(store.Started); Assert.Empty(store.Results);
    }

    [Fact]
    public async Task Interrupted_unsafe_executor_is_not_replayed()
    {
        var store = new RecordingStore { Interrupted = true };
        var calls = 0;
        var unsafeExecutor = new Executor(NodeType.WebhookTrigger, (c, _) => { calls++; return Task.FromResult(NodeResult.Success(c.Input)); }, false);
        await Engine(store, [unsafeExecutor, new LogNodeExecutor()]).RunAsync(store.Claim);
        Assert.Equal(0, calls); Assert.Equal(ExecutionFailureCode.InterruptedNode, Assert.Single(store.Results).ErrorCode);
    }

    private static SequentialExecutionEngine Engine(RecordingStore store, IEnumerable<INodeExecutor> executors, TimeSpan? timeout = null) =>
        new(store, executors, EngineOptions.Default with { NodeTimeout = timeout ?? TimeSpan.FromSeconds(10) });
    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private sealed class Executor(NodeType type, Func<NodeRunContext, CancellationToken, Task<NodeResult>> run, bool replay = true) : INodeExecutor
    {
        public NodeType Type => type;
        public bool CanReplayAfterInterruption => replay;
        public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct) => run(context, ct);
    }

    // Stub do contrato de persistência; transações/leases reais são verificadas em IntegrationTests.
    private sealed class RecordingStore : IExecutionEngineStore
    {
        public WorkflowVersionSnapshot Version { get; }
        private readonly WorkflowExecutionSnapshot execution;
        private Guid current;
        private JsonElement input = Json("{\"initial\":1}");
        public InboxClaim Claim { get; } = new(InboxClaimStatus.Acquired, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        public List<Guid> Started { get; } = [];
        public List<NodeResult> Results { get; } = [];
        public bool Lost { get; init; }
        public bool Interrupted { get; init; }
        public RecordingStore()
        {
            var now = DateTimeOffset.UtcNow; var workflow = new Workflow(Guid.NewGuid(), Guid.NewGuid(), "Engine unit", now);
            var v = workflow.CreateDraft(Guid.NewGuid(), now); var trigger = Guid.NewGuid(); var log = Guid.NewGuid();
            workflow.ReplaceDraftGraph([new(v.Id, log, NodeType.Log, new LogConfiguration("Fim")),
                new(v.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration())],
                [new(Guid.NewGuid(), v.Id, trigger, log, "next")], now); workflow.PublishDraft(now);
            Version = new(v.Id, 1, v.Status, v.CreatedAt, v.PublishedAt, v.Revision, v.Nodes, v.Connections);
            current = trigger;
            execution = new(Claim.ExecutionId, workflow.Id, v.Id, workflow.OwnerUserId, Guid.NewGuid(), WorkflowExecutionStatus.Running, now, now, null, null);
        }
        public Task<ExecutionCheckpoint?> LoadAsync(InboxClaim claim, CancellationToken ct = default) =>
            Task.FromResult<ExecutionCheckpoint?>(Lost ? null : new(execution, Version, 0, current, input,
                Version.Nodes.Select(n => new NodeExecutionSnapshot(Guid.NewGuid(), Claim.ExecutionId, Version.Id, n.NodeId,
                    Interrupted ? NodeExecutionStatus.Running : NodeExecutionStatus.Pending, 0, null, null, null, null, null)).ToArray()));
        public Task<LeaseStatus> RenewAsync(InboxClaim claim, TimeSpan lease, CancellationToken ct = default) => Task.FromResult(LeaseStatus.Active);
        public Task<NodeStart> BeginNodeAsync(InboxClaim claim, int revision, Guid nodeId, CancellationToken ct = default)
        { Started.Add(nodeId); return Task.FromResult(new NodeStart(NodeStartStatus.Started, 1)); }
        public Task<CheckpointWriteStatus> SaveNodeAsync(InboxClaim claim, int revision, Guid nodeId, NodeResult result, Guid? nextNodeId, CancellationToken ct = default)
        {
            Results.Add(result); if (result.Output.HasValue) input = result.Output.Value;
            if (nextNodeId.HasValue) current = nextNodeId.Value;
            return Task.FromResult(nextNodeId.HasValue && result.ErrorCode is null ? CheckpointWriteStatus.Saved : CheckpointWriteStatus.Completed);
        }
    }
}
