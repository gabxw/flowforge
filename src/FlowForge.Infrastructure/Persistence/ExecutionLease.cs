using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;
internal sealed record HeldExecution(WorkflowExecutionRecord Row, InboxMessageRecord Inbox, DateTimeOffset Now);
internal static class ExecutionLease
{
    // Chamador já abriu transação curta. Ordem única: execução -> inbox -> nodes.
    internal static async Task<HeldExecution?> LockAsync(FlowForgeDbContext db, InboxClaim claim, CancellationToken ct)
    {
        if (claim.Status != InboxClaimStatus.Acquired || claim.Token is null || claim.Generation <= 0) return null;
        var rows = await db.WorkflowExecutions.FromSqlInterpolated($"""
            SELECT * FROM workflow_executions WHERE id = {claim.ExecutionId} FOR UPDATE
            """).ToArrayAsync(ct);
        var row = rows.SingleOrDefault();
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(i => i.MessageId == claim.MessageId && i.ExecutionId == claim.ExecutionId, ct);
        var now = await ExecutionPersistence.NowAsync(db, ct);
        if (row is null || inbox is null || inbox.CompletedAt is not null || inbox.ClaimToken != claim.Token ||
            inbox.ClaimAttempts != claim.Generation || inbox.ClaimUntil <= now ||
            row.Status is not (WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running)) return null;
        if (!await db.OutboxMessages.AnyAsync(o => o.Id == claim.MessageId && o.ExecutionId == row.Id && o.DispatchSequence == row.DispatchSequence, ct) ||
            (row.ResumeAt > now && row.CancelRequestedAt is null)) return null;
        now = new[] { now, row.CreatedAt, row.StartedAt ?? row.CreatedAt, row.CancelRequestedAt ?? row.CreatedAt }.Max();
        return new(row, inbox, now);
    }
}
