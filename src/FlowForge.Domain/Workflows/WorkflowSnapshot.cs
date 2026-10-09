namespace FlowForge.Domain.Workflows;

public sealed record WorkflowSnapshot(Guid Id, Guid OwnerUserId, string Name, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, int Revision,
    Guid? CurrentPublishedVersionId, IReadOnlyList<WorkflowVersionSnapshot> Versions);

public sealed record WorkflowVersionSnapshot(Guid Id, int VersionNumber, WorkflowVersionStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt, int Revision,
    IReadOnlyList<WorkflowNode> Nodes, IReadOnlyList<WorkflowConnection> Connections);
