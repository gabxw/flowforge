using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

public sealed class PostgresExecutionInboxStore(IDbContextFactory<FlowForgeDbContext> factory) : IExecutionInboxStore
{
    public async Task<InboxClaim> TryClaimAsync(ExecutionRequestedMessage message, TimeSpan lease, CancellationToken ct = default)
    {
        ExecutionPersistence.ValidateLease(lease);
        InboxClaim Result(InboxClaimStatus status, Guid? token = null, int generation = 0) => new(status, message.ExecutionId, message.MessageId, token, generation);
        if (!message.IsValid()) return Result(InboxClaimStatus.Invalid);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Ordem de locks em claim e conclusão: execução, depois inbox.
        var rows = await db.WorkflowExecutions.FromSqlInterpolated($"""
            SELECT * FROM workflow_executions WHERE id = {message.ExecutionId} FOR UPDATE
            """).ToArrayAsync(ct);
        var row = rows.SingleOrDefault();
        var dispatch = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(o => o.Id == message.MessageId &&
            o.ExecutionId == message.ExecutionId && o.CorrelationId == message.CorrelationId && o.ContractVersion == message.ContractVersion, ct);
        if (row is null || row.CorrelationId != message.CorrelationId || dispatch is null)
            return Result(InboxClaimStatus.Invalid);
        var inbox = await db.InboxMessages.SingleOrDefaultAsync(i => i.MessageId == message.MessageId, ct);
        if (inbox?.CompletedAt is not null) return Result(InboxClaimStatus.Completed);
        var now = await ExecutionPersistence.NowAsync(db, ct);
        // Mensagem antiga ou adiantada não assume a continuação. A outbox permanece a fonte do agendamento.
        if (row.Status is not (WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running) ||
            dispatch.DispatchSequence != row.DispatchSequence ||
            (row.ResumeAt > now && row.CancelRequestedAt is null) ||
            (dispatch.AvailableAt > now && row.CancelRequestedAt is null))
            return Result(InboxClaimStatus.Completed);
        if (inbox?.ClaimUntil > now) return Result(InboxClaimStatus.Busy);
        var token = Guid.NewGuid();
        if (inbox is null)
        {
            inbox = new InboxMessageRecord { MessageId = message.MessageId, ExecutionId = message.ExecutionId, ReceivedAt = now };
            db.InboxMessages.Add(inbox);
        }
        inbox.ClaimToken = token; inbox.ClaimUntil = now + lease; inbox.ClaimAttempts++;
        var execution = WorkflowExecution.Restore(ExecutionPersistence.Snapshot(row));
        if (row.Status == WorkflowExecutionStatus.Pending && row.CancelRequestedAt is null)
        {
            execution.Start(now); row.Status = execution.Snapshot.Status; row.StartedAt = execution.Snapshot.StartedAt;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Result(InboxClaimStatus.Acquired, token, inbox.ClaimAttempts);
    }

}
