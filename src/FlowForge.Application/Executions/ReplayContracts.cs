
using FlowForge.Domain.Executions;

namespace FlowForge.Application.Executions;
public sealed record ExecutionReplayRequest(string IdempotencyKey, bool AcknowledgeExternalEffects);
public sealed record ReplaySummary(Guid Id, Guid OriginalExecutionId, Guid NewExecutionId, DateTimeOffset CreatedAt);
public interface IExecutionReplayStore
{
    Task<WorkflowExecutionSnapshot> ReplayAsync(Guid executionId, Guid owner, ExecutionReplayRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ReplaySummary>?> ReplaysAsync(Guid executionId, Guid owner, CancellationToken ct = default);
}
