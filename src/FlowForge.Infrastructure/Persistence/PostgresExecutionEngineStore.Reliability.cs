
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;
public sealed partial class PostgresExecutionEngineStore
{
    public async Task<LeaseStatus> BeforeExternalCallAsync(InboxClaim claim, int revision, Guid nodeId, int? credentialRevision, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var held = await ExecutionLease.LockAsync(db, claim, ct);
        if (held is null || held.Row.CheckpointRevision != revision || held.Row.NextNodeId != nodeId) return LeaseStatus.Lost;
        if (held.Row.CancelRequestedAt.HasValue) return LeaseStatus.CancelRequested;
        if (held.Row.DeadlineAt <= held.Now) return LeaseStatus.DeadlineExceeded;
        var record = await db.NodeExecutions.SingleAsync(n => n.ExecutionId == claim.ExecutionId && n.NodeId == nodeId, ct);
        var definition = await db.WorkflowNodes.AsNoTracking().SingleAsync(n => n.WorkflowVersionId == record.WorkflowVersionId && n.NodeId == nodeId, ct);
        if (definition.Type != NodeType.HttpRequest || record.Status != NodeExecutionStatus.Running ||
            (credentialRevision.HasValue && definition.CredentialId is null)) throw new ArgumentException("Journal exige HTTP ativo.");
        var attempt = await CurrentAttemptAsync(db, record, true, held.Now, ct);
        var prior = await db.NodeAttempts.AsNoTracking().Where(a => a.NodeExecutionId == record.Id && a.Number < record.AttemptCount && a.CredentialRevisionUsed != null)
            .OrderByDescending(a => a.Number).FirstOrDefaultAsync(ct);
        // Chave idempotente pode ter escopo de principal. Não repetir a mesma execução com outra revisão de segredo.
        if (prior is not null && prior.CredentialRevisionUsed != credentialRevision) throw new CredentialRevisionChangedException();
        var domain = NodeExecutionAttempt.Restore(AttemptPersistence.Snapshot(attempt));
        domain.RecordDispatch(credentialRevision, held.Now < attempt.StartedAt ? attempt.StartedAt : held.Now);
        AttemptPersistence.Apply(attempt, domain.Snapshot);
        if (credentialRevision is { } known) {
            var node = NodeExecution.Restore(NodeExecutionPersistence.Snapshot(record)); node.RecordCredentialRevision(known);
            NodeExecutionPersistence.Apply(record, node.Snapshot);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return LeaseStatus.Active;
    }

    private static async Task<NodeAttemptRecord> CurrentAttemptAsync(FlowForgeDbContext db, NodeExecutionRecord node,
        bool external, DateTimeOffset now, CancellationToken ct, bool legacy = true)
    {
        var existing = db.NodeAttempts.Local.SingleOrDefault(a => a.NodeExecutionId == node.Id && a.Number == node.AttemptCount)
            ?? await db.NodeAttempts.SingleOrDefaultAsync(a => a.NodeExecutionId == node.Id && a.Number == node.AttemptCount, ct);
        if (existing is not null) return existing;
        var attempt = new NodeExecutionAttempt(Guid.NewGuid(), node.ExecutionId, node.Id, node.AttemptCount,
            legacy ? node.StartedAt!.Value : now, external).Snapshot with { Legacy = legacy };
        var record = new NodeAttemptRecord(); AttemptPersistence.Apply(record, attempt); db.NodeAttempts.Add(record); return record;
    }
    private static void EndAttempt(NodeAttemptRecord record, DateTimeOffset now, NodeAttemptStatus status, ExecutionFailureCode? error,
        ExternalOutcome? outcome = null, int? httpStatus = null, int? revision = null, DateTimeOffset? retryAt = null)
    {
        if (record.Status != NodeAttemptStatus.Running) return;
        var domain = NodeExecutionAttempt.Restore(AttemptPersistence.Snapshot(record));
        now = new[] { now, record.StartedAt, record.DispatchedAt ?? record.StartedAt }.Max();
        domain.Finish(status, now, error, outcome ?? record.ExternalOutcome, httpStatus, revision, retryAt);
        AttemptPersistence.Apply(record, domain.Snapshot);
    }
    private static void Suspend(FlowForgeDbContext db, HeldExecution held, DateTimeOffset resumeAt, DateTimeOffset now)
    {
        var due = resumeAt > held.Row.DeadlineAt ? held.Row.DeadlineAt : resumeAt;
        var ticks = due.Ticks % 10; if (ticks != 0) due = due.AddTicks(10 - ticks);
        var execution = WorkflowExecution.Restore(ExecutionPersistence.Snapshot(held.Row));
        execution.Suspend(due); held.Row.ResumeAt = due;
        held.Row.DispatchSequence = checked(held.Row.DispatchSequence + 1);
        held.Row.CheckpointRevision++;
        held.Inbox.CompletedAt = now; held.Inbox.ClaimToken = null; held.Inbox.ClaimUntil = null;
        db.OutboxMessages.Add(new() {
            Id = Guid.NewGuid(), ExecutionId = held.Row.Id, CorrelationId = held.Row.CorrelationId,
            ContractVersion = ExecutionRequestedMessage.CurrentVersion, DispatchSequence = held.Row.DispatchSequence,
            CreatedAt = now, AvailableAt = due > now ? due : now });
    }
}
