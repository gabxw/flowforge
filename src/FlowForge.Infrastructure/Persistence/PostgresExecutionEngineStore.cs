using System.Text;
using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence.Records;
using FlowForge.Infrastructure.Persistence.Serialization;
using FlowForge.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

public sealed partial class PostgresExecutionEngineStore(IDbContextFactory<FlowForgeDbContext> factory,
    ExecutionContextProtection protection) : IExecutionEngineStore
{
    public async Task<ExecutionCheckpoint?> LoadAsync(InboxClaim claim, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var held = await ExecutionLease.LockAsync(db, claim, ct);
        if (held is null) return null;
        var row = held.Row;
        var version = await ReadVersionAsync(db, row, ct);
        var path = new ExecutionPath(version, row.OwnerUserId);
        if (row.ExecutionContextProtected is null)
        {
            // Input original permanece separado do contexto mutável. Comandos manuais/legados usam {}.
            using var empty = JsonDocument.Parse("{}");
            var initial = row.WebhookEndpointId.HasValue
                ? protection.UnprotectTrigger(row.TriggerInputProtected ?? throw new InvalidOperationException("Input inicial ausente."), row.Id)
                : empty.RootElement;
            row.ExecutionContextProtected = protection.Protect(initial, row.Id);
            row.NextNodeId = path.First;
            for (var i = 0; i < version.Nodes.Count; i++)
            {
                var node = version.Nodes[i];
                db.NodeExecutions.Add(new() { Id = Guid.NewGuid(), ExecutionId = row.Id,
                    WorkflowVersionId = row.WorkflowVersionId, NodeId = node.NodeId, Ordinal = i,
                    Status = NodeExecutionStatus.Pending });
            }
            await db.SaveChangesAsync(ct);
        }
        var nodes = await db.NodeExecutions.AsNoTracking().Where(n => n.ExecutionId == row.Id)
            .OrderBy(n => n.Ordinal).ToArrayAsync(ct);
        var input = protection.Unprotect(row.ExecutionContextProtected, row.Id);
        var result = new ExecutionCheckpoint(ExecutionPersistence.Snapshot(row), version, row.CheckpointRevision,
            row.NextNodeId ?? throw new InvalidOperationException("Checkpoint ativo sem próximo node."), input,
            nodes.Select(NodeExecutionPersistence.Snapshot).ToArray(), row.DeadlineAt - held.Now);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<LeaseStatus> RenewAsync(InboxClaim claim, TimeSpan lease, CancellationToken ct = default)
    {
        ExecutionPersistence.ValidateLease(lease);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var held = await ExecutionLease.LockAsync(db, claim, ct);
        if (held is null) return LeaseStatus.Lost;
        held.Inbox.ClaimUntil = held.Now + lease;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return held.Row.CancelRequestedAt.HasValue ? LeaseStatus.CancelRequested : held.Row.DeadlineAt <= held.Now ? LeaseStatus.DeadlineExceeded : LeaseStatus.Active;
    }

    public async Task<NodeStart> BeginNodeAsync(InboxClaim claim, int revision, Guid nodeId, bool allowReplay = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var held = await ExecutionLease.LockAsync(db, claim, ct);
        if (held is null || held.Row.CheckpointRevision != revision || held.Row.NextNodeId != nodeId)
            return new(NodeStartStatus.Lost, revision);
        var nodes = await db.NodeExecutions.Where(n => n.ExecutionId == claim.ExecutionId).ToArrayAsync(ct);
        if (held.Row.CancelRequestedAt.HasValue)
        {
            await CompleteAsync(db, held, nodes, WorkflowExecutionStatus.Cancelled, null, ct);
            await tx.CommitAsync(ct);
            ExecutionTelemetry.Finished.Add(1, new KeyValuePair<string, object?>("outcome", held.Row.Status.ToString()));
            return new(NodeStartStatus.Completed, held.Row.CheckpointRevision);
        }
        if (held.Row.DeadlineAt <= held.Now || claim.Generation > ExecutionLimits.MaximumDeliveryClaims)
        {
            await CompleteAsync(db, held, nodes, WorkflowExecutionStatus.Failed,
                held.Row.DeadlineAt <= held.Now ? ExecutionFailureCode.ExecutionDeadlineExceeded : ExecutionFailureCode.AttemptLimitExceeded, ct);
            await tx.CommitAsync(ct);
            ExecutionTelemetry.Finished.Add(1, new KeyValuePair<string, object?>("outcome", held.Row.Status.ToString())); return new(NodeStartStatus.Completed, held.Row.CheckpointRevision);
        }
        var record = nodes.Single(n => n.NodeId == nodeId);
        var node = NodeExecution.Restore(NodeExecutionPersistence.Snapshot(record));
        var definition = await db.WorkflowNodes.AsNoTracking().SingleAsync(n => n.WorkflowVersionId == record.WorkflowVersionId && n.NodeId == nodeId, ct);
        var external = definition.Type == NodeType.HttpRequest;
        var newAttempt = node.Snapshot.Status is NodeExecutionStatus.Pending or NodeExecutionStatus.Retrying;
        if (node.Snapshot.Status == NodeExecutionStatus.Retrying)
        {
            node.StartRetry(held.Now);
            var execution = WorkflowExecution.Restore(ExecutionPersistence.Snapshot(held.Row)); execution.Resume(held.Now);
            held.Row.ResumeAt = null;
        }
        else if (node.Snapshot.Status == NodeExecutionStatus.Running)
        {
            if (allowReplay && held.Row.ResumeAt is null)
            {
                var interrupted = await CurrentAttemptAsync(db, record, external, held.Now, ct);
                EndAttempt(interrupted, held.Now, NodeAttemptStatus.Interrupted, ExecutionFailureCode.InterruptedNode);
                node.Resume(); newAttempt = true;
            }
        }
        else node.Start(ExecutionPayload.Summarize(protection.Unprotect(held.Row.ExecutionContextProtected!, held.Row.Id)), held.Now);
        NodeExecutionPersistence.Apply(record, node.Snapshot);
        _ = await CurrentAttemptAsync(db, record, external, held.Now, ct, legacy: !newAttempt);
        held.Row.CheckpointRevision = checked(revision + 1);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(NodeStartStatus.Started, held.Row.CheckpointRevision, node.Snapshot.AttemptCount);
    }

    public async Task<CheckpointWriteStatus> SaveNodeAsync(InboxClaim claim, int revision, Guid nodeId,
        NodeResult result, Guid? nextNodeId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var held = await ExecutionLease.LockAsync(db, claim, ct);
        if (held is null || held.Row.CheckpointRevision != revision || held.Row.NextNodeId != nodeId)
            return CheckpointWriteStatus.Lost;
        var nodes = await db.NodeExecutions.Where(n => n.ExecutionId == claim.ExecutionId).ToArrayAsync(ct);
        var record = nodes.Single(n => n.NodeId == nodeId);
        var node = NodeExecution.Restore(NodeExecutionPersistence.Snapshot(record));
        var now = record.StartedAt > held.Now ? record.StartedAt.Value : held.Now;
        var definition = await db.WorkflowNodes.AsNoTracking().SingleAsync(n => n.WorkflowVersionId == record.WorkflowVersionId && n.NodeId == record.NodeId, ct);
        var attempt = await CurrentAttemptAsync(db, record, definition.Type == NodeType.HttpRequest, now, ct);
        if (held.Row.DeadlineAt <= now && held.Row.CancelRequestedAt is null)
        {
            result = NodeResult.Failure(ExecutionFailureCode.ExecutionDeadlineExceeded, result.CredentialRevisionUsed) with {
                ExternalOutcome = result.ExternalOutcome, HttpStatusCode = result.HttpStatusCode };
            nextNodeId = null;
        }
        if (result.CredentialRevisionUsed is { } usedRevision)
        {
            if (definition.Type != NodeType.HttpRequest || definition.CredentialId is null) throw new ArgumentException("Revisão sem credencial HTTP.");
            node.RecordCredentialRevision(usedRevision);
        }
        var successful = !result.IsCancelled && result.ErrorCode is null && result.Output.HasValue &&
            (result.SuspendFor is null || held.Row.CancelRequestedAt is null);
        if (successful)
        {
            var version = await ReadVersionAsync(db, held.Row, ct);
            if (new ExecutionPath(version, held.Row.OwnerUserId).Next(nodeId, result.Port) != nextNodeId)
                throw new ArgumentException("Checkpoint não corresponde à porta do grafo.");
            if (result.SuspendFor is { } duration)
            {
                if (version.Nodes.Single(n => n.NodeId == nodeId).Configuration is not DelayConfiguration delay ||
                    duration != delay.Duration || held.Row.ResumeAt.HasValue || node.Snapshot.Status != NodeExecutionStatus.Running)
                    throw new ArgumentException("Suspensão incompatível com o node.");
                // Prazo parte do primeiro início persistido; replay antes do commit não reinicia toda a espera.
                var deadline = node.Snapshot.StartedAt!.Value + duration;
                var remainder = deadline.Ticks % 10;
                if (remainder != 0) deadline = deadline.AddTicks(10 - remainder); // PostgreSQL: microssegundos, nunca acordar antes.
                Suspend(db, held, deadline, now);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                ExecutionTelemetry.ScheduledWait.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("kind", "delay"));
                return CheckpointWriteStatus.Suspended;
            }
            if (held.Row.ResumeAt.HasValue)
            {
                if (version.Nodes.Single(n => n.NodeId == nodeId).Type != NodeType.Delay)
                    throw new ArgumentException("Retomada incompatível com o node.");
                var execution = WorkflowExecution.Restore(ExecutionPersistence.Snapshot(held.Row));
                execution.Resume(now); held.Row.ResumeAt = null;
            }
            node.Succeed(ExecutionPayload.Summarize(result.Output!.Value), now);
            held.Row.ExecutionContextProtected = protection.Protect(result.Output.Value, held.Row.Id);
            held.Row.NextNodeId = nextNodeId;
            if (result.LogMessage is not null)
            {
                if (version.Nodes.Single(n => n.NodeId == nodeId).Type != NodeType.Log)
                    throw new ArgumentException("Somente Log pode gravar uma mensagem.");
                db.ExecutionLogs.Add(new() { Id = Guid.NewGuid(), ExecutionId = held.Row.Id,
                    NodeExecutionId = record.Id, CreatedAt = now, EventCode = ExecutionLogCode.LogRecorded,
                    MessageProtected = protection.ProtectLog(result.LogMessage, record.Id),
                    MessageByteLength = Encoding.UTF8.GetByteCount(result.LogMessage) });
            }
        }
        else if (held.Row.CancelRequestedAt.HasValue)
            node.Cancel(now);
        else if (definition.Type == NodeType.HttpRequest &&
            HttpRetryPolicy.NextDelay((HttpRequestConfiguration)(await ReadVersionAsync(db, held.Row, ct)).Nodes.Single(n => n.NodeId == nodeId).Configuration,
                node.Snapshot.AttemptCount, result, Random.Shared.NextDouble()) is { } wait &&
            now + wait < held.Row.DeadlineAt)
        {
            var retryAt = now + wait; var remainder = retryAt.Ticks % 10; if (remainder != 0) retryAt = retryAt.AddTicks(10 - remainder);
            node.Retry(result.ErrorCode!.Value, retryAt); NodeExecutionPersistence.Apply(record, node.Snapshot);
            EndAttempt(attempt, now, NodeAttemptStatus.Failed, result.ErrorCode, result.ExternalOutcome, result.HttpStatusCode, result.CredentialRevisionUsed, retryAt);
            Suspend(db, held, retryAt, now);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            ExecutionTelemetry.Retries.Add(1);
            ExecutionTelemetry.ScheduledWait.Record(wait.TotalSeconds, new KeyValuePair<string, object?>("kind", "retry"));
            return CheckpointWriteStatus.Suspended;
        }
        else
        {
            if (result.IsCancelled || result.ErrorCode is null || result.Output.HasValue)
                throw new ArgumentException("Resultado de falha inválido.");
            node.Fail(result.ErrorCode.Value, now);
        }
        NodeExecutionPersistence.Apply(record, node.Snapshot);
        EndAttempt(attempt, now, node.Snapshot.Status == NodeExecutionStatus.Succeeded ? NodeAttemptStatus.Succeeded :
            node.Snapshot.Status == NodeExecutionStatus.Cancelled ? NodeAttemptStatus.Cancelled :
            result.ErrorCode == ExecutionFailureCode.InterruptedNode ? NodeAttemptStatus.Interrupted : NodeAttemptStatus.Failed,
            node.Snapshot.Status == NodeExecutionStatus.Failed ? result.ErrorCode : null,
            result.ExternalOutcome, result.HttpStatusCode, result.CredentialRevisionUsed);
        if (held.Row.CancelRequestedAt.HasValue)
            await CompleteAsync(db, held with { Now = now }, nodes, WorkflowExecutionStatus.Cancelled, null, ct);
        else if (!successful)
            await CompleteAsync(db, held with { Now = now }, nodes, WorkflowExecutionStatus.Failed, result.ErrorCode, ct);
        else if (nextNodeId is null)
            await CompleteAsync(db, held with { Now = now }, nodes, WorkflowExecutionStatus.Succeeded, null, ct);
        else
        {
            held.Row.CheckpointRevision = checked(revision + 1);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (held.Inbox.CompletedAt.HasValue) ExecutionTelemetry.Finished.Add(1, new KeyValuePair<string, object?>("outcome", held.Row.Status.ToString()));
        return held.Inbox.CompletedAt.HasValue ? CheckpointWriteStatus.Completed : CheckpointWriteStatus.Saved;
    }

    internal static async Task CompleteAsync(FlowForgeDbContext db, HeldExecution held, NodeExecutionRecord[] nodes,
        WorkflowExecutionStatus status, ExecutionFailureCode? error, CancellationToken ct)
    {
        var now = nodes.Select(n => n.StartedAt ?? held.Now).Append(held.Now).Max();
        foreach (var record in nodes)
        {
            var node = NodeExecution.Restore(NodeExecutionPersistence.Snapshot(record));
            if (node.Snapshot.Status == NodeExecutionStatus.Pending) node.Skip(now);
            else if (node.Snapshot.Status is NodeExecutionStatus.Running or NodeExecutionStatus.Retrying)
            {
                if (status == WorkflowExecutionStatus.Cancelled) node.Cancel(now);
                else if (status == WorkflowExecutionStatus.Failed) node.Fail(error!.Value, now);
                else continue;
            }
            else continue;
            NodeExecutionPersistence.Apply(record, node.Snapshot);
        }
        var attempts = await db.NodeAttempts.Where(a => a.ExecutionId == held.Row.Id && a.Status == NodeAttemptStatus.Running).ToArrayAsync(ct);
        foreach (var attempt in attempts) EndAttempt(attempt, now,
            status == WorkflowExecutionStatus.Cancelled ? NodeAttemptStatus.Cancelled : NodeAttemptStatus.Failed,
            status == WorkflowExecutionStatus.Cancelled ? null : error);
        var execution = WorkflowExecution.Restore(ExecutionPersistence.Snapshot(held.Row));
        switch (status)
        {
            case WorkflowExecutionStatus.Succeeded: execution.Succeed(now); break;
            case WorkflowExecutionStatus.Failed: execution.Fail(error!.Value, now); break;
            case WorkflowExecutionStatus.Cancelled: execution.Cancel(now); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
        held.Row.Status = execution.Snapshot.Status; held.Row.FinishedAt = execution.Snapshot.FinishedAt;
        held.Row.ErrorCode = execution.Snapshot.ErrorCode; held.Row.NextNodeId = null; held.Row.ResumeAt = null;
        held.Row.CheckpointRevision = checked(held.Row.CheckpointRevision + 1);
        held.Inbox.CompletedAt = now; held.Inbox.ClaimToken = null; held.Inbox.ClaimUntil = null;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<WorkflowVersionSnapshot> ReadVersionAsync(FlowForgeDbContext db,
        WorkflowExecutionRecord execution, CancellationToken ct)
    {
        var version = await db.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == execution.WorkflowVersionId &&
            v.WorkflowId == execution.WorkflowId && v.OwnerUserId == execution.OwnerUserId, ct);
        var nodes = await db.WorkflowNodes.AsNoTracking().Where(n => n.WorkflowVersionId == version.Id).OrderBy(n => n.Ordinal).ToArrayAsync(ct);
        var edges = await db.WorkflowConnections.AsNoTracking().Where(c => c.WorkflowVersionId == version.Id).OrderBy(c => c.Ordinal).ToArrayAsync(ct);
        return new(version.Id, version.VersionNumber, version.Status, version.CreatedAt, version.PublishedAt, version.Revision,
            nodes.Select(n => new WorkflowNode(n.WorkflowVersionId, n.NodeId, n.Type,
                NodeConfigurationJson.Deserialize(n.Type, n.Configuration), new(n.PositionX, n.PositionY),
                n.CredentialId.HasValue ? new CredentialReference(n.CredentialId.Value, n.OwnerUserId) : null)).ToArray(),
            edges.Select(c => new WorkflowConnection(c.Id, c.WorkflowVersionId, c.SourceNodeId, c.TargetNodeId, c.SourcePort)).ToArray());
    }
}
