namespace FlowForge.Domain.Workflows;

public sealed class Workflow
{
    private readonly List<WorkflowVersion> versions = [];

    public Workflow(Guid id, Guid ownerUserId, string name, DateTimeOffset createdAt, string? description = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("O workflow deve ter uma identidade.", nameof(id));
        if (ownerUserId == Guid.Empty)
            throw new ArgumentException("O proprietário deve ter uma identidade.", nameof(ownerUserId));
        var normalizedName = ValidateName(name);
        ValidateDescription(description);

        Id = id;
        OwnerUserId = ownerUserId;
        Name = normalizedName;
        Description = description;
        CreatedAt = createdAt.ToUniversalTime();
        UpdatedAt = CreatedAt;
        Versions = versions.AsReadOnly();
    }

    public Guid Id { get; }
    public Guid OwnerUserId { get; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public int Revision { get; private set; }
    public Guid? CurrentPublishedVersionId { get; private set; }
    public WorkflowVersion? DraftVersion { get; private set; }
    public IReadOnlyList<WorkflowVersion> Versions { get; }

    public WorkflowVersion CreateDraft(Guid versionId, DateTimeOffset now)
    {
        EnsureEditable();
        if (versionId == Guid.Empty)
            throw new ArgumentException("A versão deve ter uma identidade.", nameof(versionId));
        if (DraftVersion is not null)
            throw new InvalidOperationException("O workflow já possui um rascunho ativo.");
        if (versions.Any(version => version.Id == versionId))
            throw new ArgumentException("A identidade da versão já foi utilizada.", nameof(versionId));
        var timestamp = ValidateTime(now);
        var revision = checked(Revision + 1);
        var number = checked(versions.Count + 1);

        IReadOnlyList<WorkflowNode> nodes = Array.AsReadOnly(Array.Empty<WorkflowNode>());
        IReadOnlyList<WorkflowConnection> connections = Array.AsReadOnly(Array.Empty<WorkflowConnection>());
        if (CurrentPublishedVersionId is Guid publishedId)
        {
            var published = versions.Single(version => version.Id == publishedId);
            nodes = Array.AsReadOnly(published.Nodes.Select(node =>
                new WorkflowNode(versionId, node.NodeId, node.Type, node.Configuration, node.Position, node.Credential)).ToArray());
            var connectionIds = published.Connections.Select(connection => connection.Id).ToHashSet();
            connections = Array.AsReadOnly(published.Connections.Select(connection =>
            {
                Guid connectionId;
                do { connectionId = Guid.NewGuid(); } while (!connectionIds.Add(connectionId));
                return new WorkflowConnection(connectionId, versionId, connection.SourceNodeId,
                    connection.TargetNodeId, connection.SourcePort);
            }).ToArray());
        }

        var draft = new WorkflowVersion(versionId, Id, OwnerUserId, number, timestamp, nodes, connections);
        versions.Add(draft);
        DraftVersion = draft;
        UpdatedAt = timestamp;
        Revision = revision;
        return draft;
    }

    public void ReplaceDraftGraph(IEnumerable<WorkflowNode> nodes, IEnumerable<WorkflowConnection> connections,
        DateTimeOffset now)
    {
        EnsureEditable();
        var draft = RequireDraft();
        var timestamp = ValidateTime(now);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        var nodeCopy = nodes.ToArray();
        var connectionCopy = connections.ToArray();
        if (nodeCopy.Any(node => node is null))
            throw new ArgumentException("Os nodes não podem ser nulos.", nameof(nodes));
        if (connectionCopy.Any(connection => connection is null))
            throw new ArgumentException("As conexões não podem ser nulas.", nameof(connections));
        var readonlyNodes = Array.AsReadOnly(nodeCopy);
        var readonlyConnections = Array.AsReadOnly(connectionCopy);
        var revision = checked(Revision + 1);
        var draftRevision = checked(draft.Revision + 1);

        // As duas coleções e revisões estão preparadas antes de qualquer alteração.
        draft.ReplaceGraph(readonlyNodes, readonlyConnections, draftRevision);
        UpdatedAt = timestamp;
        Revision = revision;
    }

    public WorkflowVersion PublishDraft(DateTimeOffset now)
    {
        EnsureEditable();
        var draft = RequireDraft();
        var timestamp = ValidateTime(now);
        var errors = WorkflowGraphValidator.Validate(draft.Id, OwnerUserId, draft.Nodes, draft.Connections);
        if (errors.Count != 0)
            throw new WorkflowValidationException(errors);
        var revision = checked(Revision + 1);
        var draftRevision = checked(draft.Revision + 1);

        draft.Publish(timestamp, draftRevision);
        CurrentPublishedVersionId = draft.Id;
        DraftVersion = null;
        UpdatedAt = timestamp;
        Revision = revision;
        return draft;
    }

    public void UpdateDetails(string name, string? description, DateTimeOffset now)
    {
        EnsureEditable();
        var timestamp = ValidateTime(now);
        var normalizedName = ValidateName(name);
        ValidateDescription(description);
        var revision = checked(Revision + 1);

        Name = normalizedName;
        Description = description;
        UpdatedAt = timestamp;
        Revision = revision;
    }

    public void Archive(DateTimeOffset now)
    {
        EnsureEditable();
        var timestamp = ValidateTime(now);
        var revision = checked(Revision + 1);

        ArchivedAt = timestamp;
        UpdatedAt = timestamp;
        Revision = revision;
    }

    private void EnsureEditable()
    {
        if (ArchivedAt.HasValue)
            throw new InvalidOperationException("O workflow arquivado não pode ser alterado.");
    }

    private WorkflowVersion RequireDraft() => DraftVersion ??
        throw new InvalidOperationException("O workflow não possui um rascunho ativo.");

    private DateTimeOffset ValidateTime(DateTimeOffset now)
    {
        var timestamp = now.ToUniversalTime();
        if (timestamp < UpdatedAt)
            throw new ArgumentOutOfRangeException(nameof(now), "O instante não pode retroceder a última alteração.");
        return timestamp;
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 200)
            throw new ArgumentException("O nome deve ter até 200 unidades UTF-16.", nameof(name));
        return normalized;
    }

    private static void ValidateDescription(string? description)
    {
        if (description?.Length > 2000)
            throw new ArgumentException("A descrição deve ter até 2.000 unidades UTF-16.", nameof(description));
    }
}
