using FlowForge.Application.Users;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Workflows;

public sealed record NodeDefinition(Guid NodeId, NodeConfiguration Configuration, NodePosition Position,
    Guid? CredentialId = null);
public sealed record ConnectionDefinition(Guid Id, Guid SourceNodeId, Guid TargetNodeId, string SourcePort);

public sealed class WorkflowStateConflictException(string message) : Exception(message);

public sealed class WorkflowService(IWorkflowStore workflows, ITechnicalUserStore users, TimeProvider clock)
{
    public async Task<Workflow> CreateAsync(Guid owner, string name, string? description, CancellationToken ct = default)
    {
        var now = UtcNow();
        var workflow = new Workflow(Guid.NewGuid(), owner, name, now, description);
        workflow.CreateDraft(Guid.NewGuid(), now);
        await users.EnsureExistsAsync(owner, now, ct);
        await workflows.AddAsync(workflow, ct);
        return workflow;
    }

    public async Task<Workflow> GetAsync(Guid owner, Guid id, CancellationToken ct = default) =>
        await workflows.GetAsync(id, owner, ct) ?? throw new KeyNotFoundException("Workflow não encontrado.");

    public Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid owner, int offset, int limit,
        bool includeArchived, CancellationToken ct = default) =>
        workflows.ListAsync(owner, offset, limit, includeArchived, ct);

    public async Task<Workflow> UpdateAsync(Guid owner, Guid id, int expectedRevision, string name,
        string? description, CancellationToken ct = default)
    {
        var workflow = await EditableAsync(owner, id, expectedRevision, ct);
        workflow.UpdateDetails(name, description, Now(workflow));
        return await SaveAsync(workflow, expectedRevision, ct);
    }

    public async Task<Workflow> CreateDraftAsync(Guid owner, Guid id, int expectedRevision, CancellationToken ct = default)
    {
        var workflow = await EditableAsync(owner, id, expectedRevision, ct);
        if (workflow.DraftVersion is not null)
            throw new WorkflowStateConflictException("O workflow já possui um rascunho ativo.");
        workflow.CreateDraft(Guid.NewGuid(), Now(workflow));
        return await SaveAsync(workflow, expectedRevision, ct);
    }

    public async Task<Workflow> ReplaceDraftAsync(Guid owner, Guid id, int expectedRevision,
        IReadOnlyList<NodeDefinition> nodes, IReadOnlyList<ConnectionDefinition> connections, CancellationToken ct = default)
    {
        var workflow = await EditableAsync(owner, id, expectedRevision, ct);
        var draft = RequireDraft(workflow);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        if (nodes.Count > 50 || connections.Count > 100)
            throw new ArgumentException("O rascunho aceita até 50 nodes e 100 conexões.");
        var domainNodes = nodes.Select(n => new WorkflowNode(draft.Id, n.NodeId, n.Configuration.Type,
            n.Configuration, n.Position, n.CredentialId is { } credentialId ? new CredentialReference(credentialId, owner) : null)).ToArray();
        var domainConnections = connections.Select(c => new WorkflowConnection(c.Id, draft.Id, c.SourceNodeId,
            c.TargetNodeId, c.SourcePort)).ToArray();
        // Rascunhos incompletos são válidos para edição, mas devem caber nas invariantes de armazenamento.
        var errors = WorkflowGraphValidator.Validate(draft.Id, owner, domainNodes, domainConnections)
            .Where(e => e.Code is GraphErrorCode.DuplicateNodeId or GraphErrorCode.DuplicateConnectionId
                or GraphErrorCode.MissingSourceNode or GraphErrorCode.MissingTargetNode
                or GraphErrorCode.DuplicateSourcePort).ToList();
        var historicalIds = workflow.Versions.Where(v => v.Id != draft.Id)
            .SelectMany(v => v.Connections).Select(c => c.Id).ToHashSet();
        errors.AddRange(domainConnections.Where(c => historicalIds.Contains(c.Id)).Select(c =>
            new GraphValidationError(GraphErrorCode.DuplicateConnectionId,
                "A identidade da conexão já pertence ao histórico publicado.", ConnectionId: c.Id)));
        if (errors.Count != 0) throw new WorkflowValidationException(errors);
        workflow.ReplaceDraftGraph(domainNodes, domainConnections, Now(workflow));
        return await SaveAsync(workflow, expectedRevision, ct);
    }

    public async Task<Workflow> PublishAsync(Guid owner, Guid id, int expectedRevision, CancellationToken ct = default)
    {
        var workflow = await EditableAsync(owner, id, expectedRevision, ct);
        _ = RequireDraft(workflow);
        workflow.PublishDraft(Now(workflow));
        return await SaveAsync(workflow, expectedRevision, ct);
    }

    public async Task<Workflow> ArchiveAsync(Guid owner, Guid id, int expectedRevision, CancellationToken ct = default)
    {
        var workflow = await EditableAsync(owner, id, expectedRevision, ct);
        workflow.Archive(Now(workflow));
        return await SaveAsync(workflow, expectedRevision, ct);
    }

    private async Task<Workflow> EditableAsync(Guid owner, Guid id, int revision, CancellationToken ct)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        var workflow = await GetAsync(owner, id, ct);
        if (workflow.Revision != revision) throw new WorkflowConcurrencyException();
        if (workflow.ArchivedAt.HasValue)
            throw new WorkflowStateConflictException("O workflow arquivado não pode ser alterado.");
        return workflow;
    }

    private async Task<Workflow> SaveAsync(Workflow workflow, int revision, CancellationToken ct)
    {
        await workflows.SaveAsync(workflow, revision, ct);
        return workflow;
    }

    private DateTimeOffset Now(Workflow workflow)
    {
        var now = UtcNow();
        return now < workflow.UpdatedAt ? workflow.UpdatedAt : now;
    }

    // Precisão pública de microssegundos mantém resposta e leitura subsequente equivalentes.
    private DateTimeOffset UtcNow() => new(clock.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);

    private static WorkflowVersion RequireDraft(Workflow workflow) => workflow.DraftVersion ??
        throw new WorkflowStateConflictException("O workflow não possui um rascunho ativo.");
}
