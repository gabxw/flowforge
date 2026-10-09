namespace FlowForge.Application.Workflows;
public sealed record WorkflowSummary(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, int Revision, Guid? CurrentPublishedVersionId);
