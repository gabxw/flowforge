using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Domain.Tests;
public sealed class EngineDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static NodeExecution Node() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Node_success_records_input_output_attempt_and_immutable_terminal_state()
    {
        var node = Node(); node.Start(new(2, "Object"), Now); node.Succeed(new(4, "Null"), Now.AddSeconds(1));
        Assert.Equal(NodeExecutionStatus.Succeeded, node.Snapshot.Status); Assert.Equal(1, node.Snapshot.AttemptCount);
        Assert.Equal(node.Snapshot, NodeExecution.Restore(node.Snapshot).Snapshot);
        Assert.Throws<InvalidOperationException>(() => node.Start(new(2, "Object"), Now));
        Assert.Throws<InvalidOperationException>(() => node.Fail(ExecutionFailureCode.NodeFailed, Now));
    }

    [Fact]
    public void Recovery_preserves_start_and_counts_replay_without_reseting_the_logical_node()
    {
        var node = Node(); node.Start(new(2, "Object"), Now); node.Resume();
        Assert.Equal(2, node.Snapshot.AttemptCount); Assert.Equal(Now, node.Snapshot.StartedAt);
        node.Fail(ExecutionFailureCode.InterruptedNode, Now.AddSeconds(1));
        Assert.Equal(node.Snapshot, NodeExecution.Restore(node.Snapshot).Snapshot);
        Assert.Throws<InvalidOperationException>(() => node.Resume());
    }

    [Fact]
    public void Skipped_and_cancelled_have_distinct_time_and_attempt_semantics()
    {
        var skipped = Node(); skipped.Skip(Now);
        Assert.Equal(0, skipped.Snapshot.AttemptCount); Assert.Null(skipped.Snapshot.StartedAt);
        Assert.Equal(skipped.Snapshot, NodeExecution.Restore(skipped.Snapshot).Snapshot);
        var cancelled = Node(); cancelled.Start(new(2, "Object"), Now); cancelled.Cancel(Now);
        Assert.Equal(NodeExecutionStatus.Cancelled, cancelled.Snapshot.Status); Assert.Null(cancelled.Snapshot.ErrorCode);
        Assert.Equal(cancelled.Snapshot, NodeExecution.Restore(cancelled.Snapshot).Snapshot);
        Assert.Throws<InvalidOperationException>(() => Node().Cancel(Now));
    }

    [Fact]
    public void Invalid_node_payload_state_or_time_does_not_mutate_state()
    {
        var node = Node(); var before = node.Snapshot;
        Assert.Throws<ArgumentException>(() => node.Start(new(65537, "String"), Now));
        Assert.Throws<ArgumentException>(() => node.Start(new(1, "Undefined"), Now));
        Assert.Throws<ArgumentException>(() => NodeExecution.Restore(before with { Status = NodeExecutionStatus.Succeeded }));
        Assert.Equal(before, node.Snapshot);
        node.Start(new(2, "Object"), Now); before = node.Snapshot;
        Assert.Throws<ArgumentOutOfRangeException>(() => node.Succeed(new(2, "Object"), Now.AddTicks(-1)));
        Assert.Equal(before, node.Snapshot);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Workflow_cancellation_requires_request_and_can_happen_before_or_after_start(bool started)
    {
        var execution = new WorkflowExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        Assert.Throws<InvalidOperationException>(() => execution.Cancel(Now));
        if (started) execution.Start(Now);
        execution.RequestCancellation(Now); execution.RequestCancellation(Now.AddSeconds(1));
        Assert.Equal(Now, execution.Snapshot.CancelRequestedAt);
        execution.Cancel(Now.AddSeconds(1));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, execution.Snapshot.Status);
        Assert.Equal(execution.Snapshot, WorkflowExecution.Restore(execution.Snapshot).Snapshot);
        Assert.Equal(started ? Now : (DateTimeOffset?)null, execution.Snapshot.StartedAt);
    }

    [Fact]
    public void Workflow_success_cannot_be_overwritten_by_later_cancel_request()
    {
        var execution = new WorkflowExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        execution.Start(Now); execution.Succeed(Now); var final = execution.Snapshot;
        execution.RequestCancellation(Now.AddSeconds(1)); Assert.Equal(final, execution.Snapshot);
        Assert.Equal(final, WorkflowExecution.Restore(final).Snapshot);
    }

    [Fact]
    public void Traversal_starts_at_trigger_even_when_graph_order_is_reversed_and_validates_ports()
    {
        var workflow = new Workflow(Guid.NewGuid(), Guid.NewGuid(), "Path", Now);
        var version = workflow.CreateDraft(Guid.NewGuid(), Now); var trigger = Guid.NewGuid(); var log = Guid.NewGuid();
        workflow.ReplaceDraftGraph([new(version.Id, log, NodeType.Log, new LogConfiguration("Fim")),
            new(version.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration())],
            [new(Guid.NewGuid(), version.Id, trigger, log, "next")], Now);
        workflow.PublishDraft(Now);
        var snapshot = new WorkflowVersionSnapshot(version.Id, 1, version.Status, version.CreatedAt, version.PublishedAt,
            version.Revision, version.Nodes, version.Connections);
        var path = new ExecutionPath(snapshot, workflow.OwnerUserId);
        Assert.Equal(trigger, path.First); Assert.Equal(log, path.Next(trigger, "next")); Assert.Null(path.Next(log, "next"));
        Assert.Throws<ArgumentException>(() => path.Next(trigger, "true"));
        Assert.Throws<ArgumentException>(() => new ExecutionPath(snapshot with { Status = WorkflowVersionStatus.Draft }, workflow.OwnerUserId));
    }
}
