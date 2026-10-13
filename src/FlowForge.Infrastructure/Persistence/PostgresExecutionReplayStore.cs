
using FlowForge.Application.Executions;
using FlowForge.Application.Webhooks;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;
public sealed class PostgresExecutionReplayStore(IDbContextFactory<FlowForgeDbContext> factory, ExecutionContextProtection protection) : IExecutionReplayStore
{
    public async Task<WorkflowExecutionSnapshot> ReplayAsync(Guid executionId, Guid owner, ExecutionReplayRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey); WebhookService.ValidateKey(request.IdempotencyKey);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var identity = await db.WorkflowExecutions.AsNoTracking().Where(e => e.Id == executionId && e.OwnerUserId == owner)
            .Select(e => e.WorkflowId).SingleOrDefaultAsync(ct);
        if (identity == Guid.Empty) throw new KeyNotFoundException("Execução não encontrada.");
        // Compatível com o aceite: workflow -> execução. Replay não altera rascunho/publicação.
        _ = await db.Workflows.FromSqlInterpolated($"SELECT * FROM workflows WHERE id = {identity} AND owner_user_id = {owner} FOR UPDATE").ToArrayAsync(ct);
        var original = (await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {executionId} AND owner_user_id = {owner} FOR UPDATE").ToArrayAsync(ct)).Single();
        if (original.Status is not (WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled))
            throw new WorkflowStateConflictException("Replay exige execução Failed ou Cancelled.");
        if (!request.AcknowledgeExternalEffects && await db.WorkflowNodes.AnyAsync(n => n.WorkflowVersionId == original.WorkflowVersionId && n.Type == NodeType.HttpRequest, ct))
            throw new WorkflowStateConflictException("Replay com HTTP exige reconhecer possíveis efeitos externos anteriores.");
        var digest = WebhookSecrets.KeyDigest(request.IdempotencyKey);
        var previous = await db.ExecutionReplays.AsNoTracking().SingleOrDefaultAsync(r => r.OriginalExecutionId == original.Id && r.KeyDigest == digest, ct);
        if (previous is not null) return ExecutionPersistence.Snapshot(await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == previous.NewExecutionId, ct));
        var now = await ExecutionPersistence.NowAsync(db, ct);
        var snapshot = new WorkflowExecution(Guid.NewGuid(), original.WorkflowId, original.WorkflowVersionId, owner, Guid.NewGuid(), now).Snapshot
            with { DeadlineAt = now + ExecutionLimits.MaximumDuration };
        var trigger = original.TriggerInputProtected is null ? null :
            protection.ProtectTrigger(protection.UnprotectTrigger(original.TriggerInputProtected, original.Id), snapshot.Id);
        db.WorkflowExecutions.Add(new() { Id = snapshot.Id, WorkflowId = snapshot.WorkflowId, WorkflowVersionId = snapshot.WorkflowVersionId,
            OwnerUserId = owner, CorrelationId = snapshot.CorrelationId, Status = snapshot.Status, TraceParent = ExecutionTelemetry.CaptureParent(), CreatedAt = now, DeadlineAt = snapshot.DeadlineAt!.Value,
            WebhookEndpointId = original.WebhookEndpointId, TriggerInputProtected = trigger });
        db.OutboxMessages.Add(new() { Id = Guid.NewGuid(), ExecutionId = snapshot.Id, CorrelationId = snapshot.CorrelationId,
            ContractVersion = ExecutionRequestedMessage.CurrentVersion, CreatedAt = now, AvailableAt = now });
        db.ExecutionReplays.Add(new() { Id = Guid.NewGuid(), OriginalExecutionId = original.Id, NewExecutionId = snapshot.Id,
            WorkflowId = original.WorkflowId, OwnerUserId = owner, KeyDigest = digest, CreatedAt = now });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        ExecutionTelemetry.Accepted.Add(1, new KeyValuePair<string, object?>("ingress", "replay")); return snapshot;
    }
    public async Task<IReadOnlyList<ReplaySummary>?> ReplaysAsync(Guid executionId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.WorkflowExecutions.AnyAsync(e => e.Id == executionId && e.OwnerUserId == owner, ct)) return null;
        return await db.ExecutionReplays.AsNoTracking().Where(r => r.OriginalExecutionId == executionId && r.OwnerUserId == owner)
            .OrderBy(r => r.CreatedAt).Select(r => new ReplaySummary(r.Id, r.OriginalExecutionId, r.NewExecutionId, r.CreatedAt)).ToArrayAsync(ct);
    }
}
