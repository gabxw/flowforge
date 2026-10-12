
using FlowForge.Application.Executions;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;
public sealed class PostgresExecutionRecoveryStore(IDbContextFactory<FlowForgeDbContext> factory) : IExecutionRecoveryStore
{
    public async Task<bool> RecoverOneAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Sem locks durante publish. Ordem global: execução -> inbox -> outbox.
        var rows = await db.WorkflowExecutions.FromSqlRaw("""
            SELECT e.* FROM workflow_executions e
            JOIN outbox_messages o ON o.execution_id = e.id AND o.dispatch_sequence = e.dispatch_sequence
            LEFT JOIN inbox_messages i ON i.message_id = o.id AND i.completed_at IS NULL
            WHERE e.status IN (1, 2) AND o.published_at IS NOT NULL
              AND (i.claim_until IS NULL OR i.claim_until <= clock_timestamp())
              AND (o.claim_until IS NULL OR o.claim_until <= clock_timestamp())
              AND (o.recovery_not_before IS NULL OR o.recovery_not_before <= clock_timestamp())
              AND (e.deadline_at <= clock_timestamp() OR e.cancel_requested_at IS NOT NULL OR i.claim_until <= clock_timestamp())
            ORDER BY e.deadline_at, e.id LIMIT 1 FOR UPDATE OF e SKIP LOCKED
            """).ToArrayAsync(ct);
        if (rows.SingleOrDefault() is not { } row) return false;
        var dispatch = await db.OutboxMessages.SingleAsync(o => o.ExecutionId == row.Id && o.DispatchSequence == row.DispatchSequence, ct);
        var now = await ExecutionPersistence.NowAsync(db, ct);
        dispatch.PublishedAt = null; dispatch.ClaimToken = null; dispatch.ClaimUntil = null; dispatch.AvailableAt = now;
        dispatch.RecoveryNotBefore = now + TimeSpan.FromSeconds(30);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }
    public async Task<IReadOnlyList<DispatchSummary>?> DispatchesAsync(Guid executionId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.WorkflowExecutions.AnyAsync(e => e.Id == executionId && e.OwnerUserId == owner, ct)) return null;
        return await db.OutboxMessages.AsNoTracking().Where(o => o.ExecutionId == executionId).OrderBy(o => o.DispatchSequence)
            .Select(o => new DispatchSummary(o.Id, o.DispatchSequence, o.AvailableAt, o.PublishedAt, o.PublishAttempts)).ToArrayAsync(ct);
    }
    public async Task<RedriveSummary> RedriveAsync(Guid executionId, Guid owner, RedriveRequest request, CancellationToken ct = default)
    {
        if (request.MessageId == Guid.Empty || request.ExpectedPublishAttempts < 0) throw new ArgumentException("Redrive inválido.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.WorkflowExecutions.FromSqlInterpolated($"SELECT * FROM workflow_executions WHERE id = {executionId} AND owner_user_id = {owner} FOR UPDATE").ToArrayAsync(ct)).SingleOrDefault()
            ?? throw new KeyNotFoundException("Execução não encontrada.");
        var previous = await db.ExecutionRedrives.AsNoTracking().SingleOrDefaultAsync(r => r.ExecutionId == row.Id && r.MessageId == request.MessageId && r.ExpectedPublishAttempts == request.ExpectedPublishAttempts, ct);
        if (previous is not null) return Summary(previous);
        var dispatch = (await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE id = {request.MessageId} AND execution_id = {row.Id} AND dispatch_sequence = {row.DispatchSequence} FOR UPDATE").ToArrayAsync(ct)).SingleOrDefault()
            ?? throw new WorkflowStateConflictException("Mensagem não corresponde à continuação atual.");
        var now = await ExecutionPersistence.NowAsync(db, ct);
        if (row.Status is not (WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running) ||
            dispatch.PublishAttempts != request.ExpectedPublishAttempts || dispatch.ClaimUntil > now ||
            await db.InboxMessages.AnyAsync(i => i.ExecutionId == row.Id && i.CompletedAt == null && i.ClaimUntil > now, ct))
            throw new WorkflowStateConflictException("Redrive exige entrega atual sem lease válida e revisão conhecida.");
        var available = row.CancelRequestedAt.HasValue || row.DeadlineAt <= now ? now :
            row.ResumeAt > now ? row.ResumeAt.Value : now;
        dispatch.PublishedAt = null; dispatch.ClaimToken = null; dispatch.ClaimUntil = null;
        dispatch.AvailableAt = available; dispatch.RecoveryNotBefore = now + TimeSpan.FromSeconds(30);
        var audit = new Records.ExecutionRedriveRecord { Id = Guid.NewGuid(), ExecutionId = row.Id, MessageId = dispatch.Id,
            ExpectedPublishAttempts = request.ExpectedPublishAttempts, RequestedAt = now };
        db.ExecutionRedrives.Add(audit); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Summary(audit);
    }
    public async Task<IReadOnlyList<RedriveSummary>?> RedrivesAsync(Guid executionId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.WorkflowExecutions.AnyAsync(e => e.Id == executionId && e.OwnerUserId == owner, ct)) return null;
        return (await db.ExecutionRedrives.AsNoTracking().Where(r => r.ExecutionId == executionId).OrderBy(r => r.RequestedAt).ToArrayAsync(ct)).Select(Summary).ToArray();
    }
    private static RedriveSummary Summary(Records.ExecutionRedriveRecord r) => new(r.Id, r.ExecutionId, r.MessageId, r.ExpectedPublishAttempts, r.RequestedAt);
}
