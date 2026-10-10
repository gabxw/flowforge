using FlowForge.Application.Executions;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;

namespace FlowForge.Infrastructure.Persistence;

public sealed class PostgresExecutionStore(IDbContextFactory<FlowForgeDbContext> factory) : IExecutionStore
{
    public async Task<WorkflowExecutionSnapshot> RequestAsync(ExecutionRequest request, CancellationToken ct = default)
    {
        if (new[] { request.ExecutionId, request.MessageId, request.CorrelationId, request.WorkflowId, request.OwnerUserId }.Contains(Guid.Empty))
            throw new ArgumentException("Identidades de execução não podem ser vazias.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Mesmo lock usado pelo CAS da edição: publicação/arquivamento e aceite têm ordem definida.
        var roots = await db.Workflows.FromSqlInterpolated($"""
            SELECT * FROM workflows WHERE id = {request.WorkflowId} AND owner_user_id = {request.OwnerUserId} FOR UPDATE
            """).AsNoTracking().ToArrayAsync(ct);
        var root = roots.SingleOrDefault() ?? throw new KeyNotFoundException("Workflow não encontrado.");
        if (root.ArchivedAt is not null || root.CurrentPublishedVersionId is null)
            throw new WorkflowStateConflictException("Solicitar execução exige um workflow ativo e publicado.");
        var version = await db.WorkflowVersions.AsNoTracking().SingleAsync(v =>
            v.Id == root.CurrentPublishedVersionId && v.WorkflowId == root.Id && v.OwnerUserId == root.OwnerUserId, ct);
        if (version.Status != WorkflowVersionStatus.Published)
            throw new WorkflowStateConflictException("A versão de execução precisa estar publicada.");
        var now = await ExecutionPersistence.NowAsync(db, ct);
        var execution = new WorkflowExecution(request.ExecutionId, root.Id, version.Id, root.OwnerUserId, request.CorrelationId, now).Snapshot;
        db.WorkflowExecutions.Add(new()
        {
            Id = execution.Id, WorkflowId = execution.WorkflowId, WorkflowVersionId = execution.WorkflowVersionId,
            OwnerUserId = execution.OwnerUserId, CorrelationId = execution.CorrelationId, CreatedAt = now, Status = execution.Status
        });
        db.OutboxMessages.Add(new()
        {
            Id = request.MessageId, ExecutionId = execution.Id, CorrelationId = execution.CorrelationId,
            ContractVersion = ExecutionRequestedMessage.CurrentVersion, CreatedAt = now, AvailableAt = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return execution;
    }

    public async Task<WorkflowExecutionSnapshot?> GetAsync(Guid executionId, Guid owner, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.WorkflowExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.Id == executionId && e.OwnerUserId == owner, ct);
        return row is null ? null : ExecutionPersistence.Snapshot(row);
    }
}
