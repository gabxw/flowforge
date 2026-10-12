
using FlowForge.Domain.Executions;

namespace FlowForge.Application.Executions;
public sealed class ExecutionRecoveryService(IExecutionRecoveryStore recovery, IExecutionReplayStore replay, IExecutionStore executions)
{
    public async Task<IReadOnlyList<DispatchSummary>> DispatchesAsync(Guid owner, Guid id, CancellationToken ct) =>
        await recovery.DispatchesAsync(id, owner, ct) ?? throw new KeyNotFoundException("Execução não encontrada.");
    public async Task<IReadOnlyList<RedriveSummary>> RedrivesAsync(Guid owner, Guid id, CancellationToken ct) =>
        await recovery.RedrivesAsync(id, owner, ct) ?? throw new KeyNotFoundException("Execução não encontrada.");
    public async Task<IReadOnlyList<ReplaySummary>> ReplaysAsync(Guid owner, Guid id, CancellationToken ct) =>
        await replay.ReplaysAsync(id, owner, ct) ?? throw new KeyNotFoundException("Execução não encontrada.");
    public async Task<IReadOnlyList<NodeAttemptSnapshot>> AttemptsAsync(Guid owner, Guid id, CancellationToken ct) =>
        (await executions.HistoryAsync(id, owner, ct) ?? throw new KeyNotFoundException("Execução não encontrada.")).Attempts ?? [];
    public Task<RedriveSummary> RedriveAsync(Guid owner, Guid id, RedriveRequest request, CancellationToken ct) =>
        recovery.RedriveAsync(id, owner, request, ct);
    public Task<WorkflowExecutionSnapshot> ReplayAsync(Guid owner, Guid id, ExecutionReplayRequest request, CancellationToken ct) =>
        replay.ReplayAsync(id, owner, request, ct);
}
