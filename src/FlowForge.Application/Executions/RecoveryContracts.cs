
using FlowForge.Domain.Executions;

namespace FlowForge.Application.Executions;
public sealed record DispatchSummary(Guid MessageId, int Sequence, DateTimeOffset AvailableAt, DateTimeOffset? PublishedAt, int PublishAttempts);
public sealed record RedriveRequest(Guid MessageId, int ExpectedPublishAttempts);
public sealed record RedriveSummary(Guid Id, Guid ExecutionId, Guid MessageId, int ExpectedPublishAttempts, DateTimeOffset RequestedAt);
public interface IExecutionRecoveryStore
{
    Task<bool> RecoverOneAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DispatchSummary>?> DispatchesAsync(Guid executionId, Guid owner, CancellationToken ct = default);
    Task<RedriveSummary> RedriveAsync(Guid executionId, Guid owner, RedriveRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<RedriveSummary>?> RedrivesAsync(Guid executionId, Guid owner, CancellationToken ct = default);
}
