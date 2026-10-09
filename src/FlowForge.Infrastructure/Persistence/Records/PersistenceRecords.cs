using FlowForge.Domain.Workflows;
namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class TechnicalUserRecord { public Guid Id { get; set; } public DateTimeOffset CreatedAt { get; set; } }
internal sealed class WorkflowRecord
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public int Revision { get; set; }
    public Guid? CurrentPublishedVersionId { get; set; }
}
internal sealed class WorkflowVersionRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid OwnerUserId { get; set; }
    public int VersionNumber { get; set; }
    public WorkflowVersionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Revision { get; set; }
}
internal sealed class WorkflowNodeRecord
{
    public Guid WorkflowVersionId { get; set; }
    public Guid NodeId { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid OwnerUserId { get; set; }
    public NodeType Type { get; set; }
    public string Configuration { get; set; } = "";
    public Guid? CredentialId { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public int Ordinal { get; set; }
}
internal sealed class WorkflowConnectionRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public string SourcePort { get; set; } = "";
    public int Ordinal { get; set; }
}
