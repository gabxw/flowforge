
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;

namespace FlowForge.Domain.Tests;
public sealed class ExecutionRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    [Theory] [InlineData(HttpRequestMethod.Post)] [InlineData(HttpRequestMethod.Patch)] [InlineData(HttpRequestMethod.Put)] [InlineData(HttpRequestMethod.Delete)]
    public void Effectful_methods_need_an_explicit_idempotency_contract(HttpRequestMethod method)
    {
        Assert.Throws<ArgumentException>(() => new HttpRequestConfiguration(new("https://example.com"), method, 3));
        Assert.True(new HttpRequestConfiguration(new("https://example.com"), method, 3, true).CanRepeat);
        Assert.False(new HttpRequestConfiguration(new("https://example.com"), method).CanRepeat);
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(6)] [InlineData(int.MaxValue)]
    public void Retry_budget_cannot_be_unbounded(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpRequestConfiguration(new("https://example.com"), HttpRequestMethod.Get, count));
    [Fact]
    public void Retrying_preserves_first_start_and_refuses_early_resume()
    {
        var node = new NodeExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        node.Start(new(2, "Object"), Now); node.RecordCredentialRevision(1); node.Retry(ExecutionFailureCode.NodeTimeout, Now.AddSeconds(2));
        var restored = NodeExecution.Restore(node.Snapshot);
        Assert.Throws<InvalidOperationException>(() => restored.StartRetry(Now.AddSeconds(1)));
        restored.StartRetry(Now.AddSeconds(2)); Assert.Equal(2, restored.Snapshot.AttemptCount);
        Assert.Equal(Now, restored.Snapshot.StartedAt); Assert.Null(restored.Snapshot.RetryAt); Assert.Null(restored.Snapshot.CredentialRevisionUsed);
        restored.Retry(ExecutionFailureCode.NodeTimeout, Now.AddSeconds(4)); restored.Fail(ExecutionFailureCode.ExecutionDeadlineExceeded, Now.AddSeconds(3));
        Assert.Null(restored.Snapshot.RetryAt);
    }
    [Fact]
    public void Journal_preserves_unknown_effect_and_terminal_attempt_cannot_be_overwritten()
    {
        var attempt = new NodeExecutionAttempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Now, true);
        Assert.Equal(ExternalOutcome.Unknown, attempt.Snapshot.ExternalOutcome);
        attempt.RecordDispatch(2, Now.AddSeconds(1));
        Assert.Throws<InvalidOperationException>(() => attempt.RecordDispatch(2, Now.AddSeconds(2)));
        attempt.Finish(NodeAttemptStatus.Failed, Now.AddSeconds(3), ExecutionFailureCode.NodeTimeout, ExternalOutcome.Unknown, credentialRevision: 2, retryAt: Now.AddSeconds(4));
        var snapshot = attempt.Snapshot; Assert.Equal(snapshot, NodeExecutionAttempt.Restore(snapshot).Snapshot);
        Assert.Throws<InvalidOperationException>(() => attempt.Finish(NodeAttemptStatus.Succeeded, Now.AddSeconds(5), null, ExternalOutcome.KnownSuccess));
    }
}
