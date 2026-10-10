using FlowForge.Domain.Executions;
using Xunit;
namespace FlowForge.Domain.Tests;

public sealed class WorkflowSuspensionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static WorkflowExecution Execution() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
    [Fact]
    public void Suspension_preserves_running_state_and_the_original_start()
    {
        var execution = Execution(); execution.Start(Now); execution.Suspend(Now.AddHours(1));
        Assert.Equal(WorkflowExecutionStatus.Running, execution.Snapshot.Status); Assert.Equal(Now, execution.Snapshot.StartedAt);
        Assert.Equal(execution.Snapshot, WorkflowExecution.Restore(execution.Snapshot).Snapshot);
        Assert.Throws<ArgumentOutOfRangeException>(() => execution.Resume(Now));
        Assert.Throws<InvalidOperationException>(() => execution.Suspend(Now.AddHours(2)));
        execution.Resume(Now.AddHours(1)); Assert.Null(execution.Snapshot.ResumeAt); Assert.Equal(Now, execution.Snapshot.StartedAt);
    }
    [Fact]
    public void Pending_cannot_suspend_and_only_a_persisted_wait_can_resume()
    {
        var execution = Execution(); Assert.Throws<InvalidOperationException>(() => execution.Suspend(Now.AddHours(1)));
        execution.Start(Now); Assert.Throws<InvalidOperationException>(() => execution.Resume(Now.AddHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => execution.Suspend(Now));
    }
    [Theory] [InlineData(WorkflowExecutionStatus.Pending)] [InlineData(WorkflowExecutionStatus.Succeeded)] [InlineData(WorkflowExecutionStatus.Failed)]
    public void Restore_rejects_resume_at_outside_running(WorkflowExecutionStatus status)
    {
        var execution = Execution(); execution.Start(Now);
        if (status == WorkflowExecutionStatus.Succeeded) execution.Succeed(Now);
        if (status == WorkflowExecutionStatus.Failed) execution.Fail(ExecutionFailureCode.NodeFailed, Now);
        var snapshot = status == WorkflowExecutionStatus.Pending ? Execution().Snapshot : execution.Snapshot;
        Assert.Throws<ArgumentException>(() => WorkflowExecution.Restore(snapshot with { ResumeAt = Now.AddHours(1) }));
    }
    [Fact]
    public void Cancellation_and_failure_clear_a_wait_without_waiting_for_the_deadline()
    {
        var execution = Execution(); execution.Start(Now); execution.Suspend(Now.AddHours(24));
        execution.RequestCancellation(Now.AddMinutes(1)); execution.Cancel(Now.AddMinutes(1));
        Assert.Null(execution.Snapshot.ResumeAt); Assert.Equal(WorkflowExecutionStatus.Cancelled, WorkflowExecution.Restore(execution.Snapshot).Snapshot.Status);
        var failed = Execution(); failed.Start(Now); failed.Suspend(Now.AddHours(24)); failed.Fail(ExecutionFailureCode.NodeFailed, Now.AddMinutes(1));
        Assert.Null(failed.Snapshot.ResumeAt);
    }
}
