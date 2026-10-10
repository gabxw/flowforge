using FlowForge.Domain.Executions;

namespace FlowForge.Infrastructure.Persistence.Records;

internal sealed class WorkflowExecutionRecord
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid CorrelationId { get; set; }
    public WorkflowExecutionStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ExecutionFailureCode? ErrorCode { get; set; }
    public DateTimeOffset? CancelRequestedAt { get; set; }
    public Guid? NextNodeId { get; set; }
    public byte[]? ExecutionContextProtected { get; set; }
    public int CheckpointRevision { get; set; }
}

internal sealed class OutboxMessageRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid CorrelationId { get; set; }
    public int ContractVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimUntil { get; set; }
    public int PublishAttempts { get; set; }
}

internal sealed class InboxMessageRecord
{
    public Guid MessageId { get; set; }
    public Guid ExecutionId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimUntil { get; set; }
    public int ClaimAttempts { get; set; }
}
