using FlowForge.Domain.Executions;
using Xunit;

namespace FlowForge.Domain.Tests;

public sealed class WorkflowExecutionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static WorkflowExecution New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start);

    [Fact]
    public void Request_is_pending_and_failure_requires_start()
    {
        var execution = New();
        Assert.Equal(WorkflowExecutionStatus.Pending, execution.Snapshot.Status);
        Assert.Null(execution.Snapshot.StartedAt);
        Assert.Throws<InvalidOperationException>(() => execution.Fail(ExecutionFailureCode.EngineUnavailable, Start));
        execution.Start(Start.AddSeconds(1));
        execution.Fail(ExecutionFailureCode.EngineUnavailable, Start.AddSeconds(2));
        Assert.Equal(WorkflowExecutionStatus.Failed, execution.Snapshot.Status);
        Assert.Equal(ExecutionFailureCode.EngineUnavailable, execution.Snapshot.ErrorCode);
        Assert.Equal(execution.Snapshot, WorkflowExecution.Restore(execution.Snapshot).Snapshot);
        Assert.Throws<InvalidOperationException>(() => execution.Start(Start.AddSeconds(3)));
        Assert.Throws<InvalidOperationException>(() => execution.Fail(ExecutionFailureCode.EngineUnavailable, Start.AddSeconds(3)));
    }

    [Fact]
    public void Invalid_time_and_error_do_not_mutate_state()
    {
        var execution = New();
        var before = execution.Snapshot;
        Assert.Throws<ArgumentOutOfRangeException>(() => execution.Start(Start.AddTicks(-1)));
        Assert.Equal(before, execution.Snapshot);
        execution.Start(Start.AddSeconds(1));
        var running = execution.Snapshot;
        Assert.Throws<ArgumentOutOfRangeException>(() => execution.Fail(ExecutionFailureCode.EngineUnavailable, Start));
        Assert.Throws<ArgumentOutOfRangeException>(() => execution.Fail((ExecutionFailureCode)99, Start.AddSeconds(2)));
        Assert.Equal(running, execution.Snapshot);
    }

    [Fact]
    public void Empty_identity_and_inconsistent_persisted_state_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowExecution(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Start));
        var snapshot = New().Snapshot;
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Restore(snapshot with { Status = WorkflowExecutionStatus.Running }));
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Restore(snapshot with { FinishedAt = Start }));
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Restore(snapshot with { Status = WorkflowExecutionStatus.Failed }));
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Restore(snapshot with
        {
            Status = WorkflowExecutionStatus.Failed, StartedAt = Start, FinishedAt = Start, ErrorCode = (ExecutionFailureCode)99
        }));
    }
}
