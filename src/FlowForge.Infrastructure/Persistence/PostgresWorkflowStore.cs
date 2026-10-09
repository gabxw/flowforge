using System.Data;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
namespace FlowForge.Infrastructure.Persistence;

public sealed class PostgresWorkflowStore(IDbContextFactory<FlowForgeDbContext> factory) : IWorkflowStore
{
    public async Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default)
    {
        var snapshot = WorkflowPersistenceMapper.Capture(workflow);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        context.Workflows.Add(WorkflowPersistenceMapper.Root(snapshot));
        await context.SaveChangesAsync(cancellationToken);
        foreach (var version in snapshot.Versions)
        {
            context.WorkflowVersions.Add(WorkflowPersistenceMapper.Version(snapshot, version));
            await context.SaveChangesAsync(cancellationToken);
            await InsertGraphAsync(context, snapshot, version, cancellationToken);
        }
        await SetPublicationAsync(context, snapshot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Workflow?> GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(workflowId, nameof(workflowId));
        ValidateIdentity(ownerUserId, nameof(ownerUserId));
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        // Todas as partes do grafo devem vir do mesmo snapshot MVCC.
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var root = await context.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == workflowId && w.OwnerUserId == ownerUserId, cancellationToken);
        var workflow = root is null ? null : Workflow.Restore(await WorkflowPersistenceMapper.ReadAsync(context, root, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return workflow;
    }

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid ownerUserId, int offset = 0, int limit = 20,
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(ownerUserId, nameof(ownerUserId));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Workflows.AsNoTracking().Where(w => w.OwnerUserId == ownerUserId && (includeArchived || w.ArchivedAt == null))
            .OrderByDescending(w => w.UpdatedAt).ThenByDescending(w => w.Id).Skip(offset).Take(limit)
            .Select(w => new WorkflowSummary(w.Id, w.Name, w.Description, w.CreatedAt, w.UpdatedAt,
                w.ArchivedAt, w.Revision, w.CurrentPublishedVersionId)).ToArrayAsync(cancellationToken);
    }

    public async Task SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (expectedRevision < 0 || workflow.Revision <= expectedRevision)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision), "A gravação exige uma revisão nova e a revisão originalmente lida.");
        var snapshot = WorkflowPersistenceMapper.Capture(workflow);
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var root = await context.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == snapshot.Id && w.OwnerUserId == snapshot.OwnerUserId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow não encontrado.");
        if (root.Revision != expectedRevision) throw new WorkflowConcurrencyException();
        if (root.ArchivedAt is not null) throw new InvalidOperationException("Workflow arquivado é terminal.");
        if (root.CreatedAt != snapshot.CreatedAt || snapshot.UpdatedAt < root.UpdatedAt)
            throw new ArgumentException("Identidade temporal ou histórico do workflow inconsistente.", nameof(workflow));
        var updated = await context.Workflows.Where(w => w.Id == snapshot.Id && w.OwnerUserId == snapshot.OwnerUserId && w.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.Revision, snapshot.Revision)
                .SetProperty(w => w.Name, snapshot.Name).SetProperty(w => w.Description, snapshot.Description)
                .SetProperty(w => w.UpdatedAt, snapshot.UpdatedAt).SetProperty(w => w.ArchivedAt, snapshot.ArchivedAt), cancellationToken);
        if (updated != 1) throw new WorkflowConcurrencyException();
        // O CAS mantém o lock da raiz até commit/rollback; outros saves não podem editar seu histórico.
        var previous = await WorkflowPersistenceMapper.ReadAsync(context, root, cancellationToken);
        ValidateHistory(previous, snapshot);
        foreach (var previousVersion in previous.Versions.Where(v => v.Status == WorkflowVersionStatus.Draft))
        {
            var version = snapshot.Versions[previousVersion.VersionNumber - 1];
            await context.WorkflowConnections.Where(c => c.WorkflowVersionId == version.Id).ExecuteDeleteAsync(cancellationToken);
            await context.WorkflowNodes.Where(n => n.WorkflowVersionId == version.Id).ExecuteDeleteAsync(cancellationToken);
            // Promover o rascunho antigo antes de inserir outro respeita o índice parcial.
            await context.WorkflowVersions.Where(v => v.Id == version.Id && v.WorkflowId == snapshot.Id && v.OwnerUserId == snapshot.OwnerUserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(v => v.Status, version.Status)
                    .SetProperty(v => v.PublishedAt, version.PublishedAt).SetProperty(v => v.Revision, version.Revision), cancellationToken);
            await InsertGraphAsync(context, snapshot, version, cancellationToken);
        }
        foreach (var version in snapshot.Versions.Skip(previous.Versions.Count))
        {
            context.WorkflowVersions.Add(WorkflowPersistenceMapper.Version(snapshot, version));
            await context.SaveChangesAsync(cancellationToken);
            await InsertGraphAsync(context, snapshot, version, cancellationToken);
        }
        await SetPublicationAsync(context, snapshot, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task InsertGraphAsync(FlowForgeDbContext context, WorkflowSnapshot root,
        WorkflowVersionSnapshot version, CancellationToken cancellationToken)
    {
        context.WorkflowNodes.AddRange(WorkflowPersistenceMapper.Nodes(root, version));
        await context.SaveChangesAsync(cancellationToken);
        context.WorkflowConnections.AddRange(WorkflowPersistenceMapper.Connections(version));
        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task SetPublicationAsync(FlowForgeDbContext context, WorkflowSnapshot snapshot, CancellationToken cancellationToken)
    {
        await context.Workflows.Where(w => w.Id == snapshot.Id && w.OwnerUserId == snapshot.OwnerUserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(w => w.CurrentPublishedVersionId, snapshot.CurrentPublishedVersionId), cancellationToken);
    }

    private static void ValidateHistory(WorkflowSnapshot previous, WorkflowSnapshot current)
    {
        if (current.Versions.Count < previous.Versions.Count)
            throw new ArgumentException("Nenhuma versão persistida pode desaparecer.");
        for (var index = 0; index < previous.Versions.Count; index++)
        {
            var before = previous.Versions[index]; var after = current.Versions[index];
            if (before.Id != after.Id || before.VersionNumber != after.VersionNumber || before.CreatedAt != after.CreatedAt)
                throw new ArgumentException("A identidade de uma versão persistida não pode mudar.");
            var sameGraph = WorkflowPersistenceMapper.SameGraph(before, after);
            if (before.Status == WorkflowVersionStatus.Published)
            {
                if (after.Status != before.Status || after.PublishedAt != before.PublishedAt || after.Revision != before.Revision || !sameGraph)
                    throw new ArgumentException("Uma publicação persistida é imutável.");
            }
            else if (after.Revision < before.Revision ||
                ((!sameGraph || after.Status != before.Status) && after.Revision == before.Revision))
                throw new ArgumentException("Alterar o rascunho exige uma revisão nova.");
        }
    }

    private static void ValidateIdentity(Guid id, string parameter)
    {
        if (id == Guid.Empty) throw new ArgumentException("A identidade não pode ser vazia.", parameter);
    }
}
