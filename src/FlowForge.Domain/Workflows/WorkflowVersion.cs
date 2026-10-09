namespace FlowForge.Domain.Workflows;

public sealed class WorkflowVersion
{
    internal WorkflowVersion(Guid id, Guid workflowId, Guid ownerUserId, int versionNumber,
        DateTimeOffset createdAt, IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowConnection> connections)
    {
        Id = id;
        WorkflowId = workflowId;
        OwnerUserId = ownerUserId;
        VersionNumber = versionNumber;
        CreatedAt = createdAt;
        Nodes = nodes;
        Connections = connections;
    }

    public Guid Id { get; }
    public Guid WorkflowId { get; }
    public Guid OwnerUserId { get; }
    public int VersionNumber { get; }
    public WorkflowVersionStatus Status { get; private set; } = WorkflowVersionStatus.Draft;
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public int Revision { get; private set; }
    public IReadOnlyList<WorkflowNode> Nodes { get; private set; }
    public IReadOnlyList<WorkflowConnection> Connections { get; private set; }

    internal void ReplaceGraph(IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowConnection> connections,
        int revision)
    {
        Nodes = nodes;
        Connections = connections;
        Revision = revision;
    }

    internal void Publish(DateTimeOffset publishedAt, int revision)
    {
        Status = WorkflowVersionStatus.Published;
        PublishedAt = publishedAt;
        Revision = revision;
    }
}
