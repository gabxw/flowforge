
using FlowForge.Domain.Executions;

namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class NodeAttemptRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid NodeExecutionId { get; set; }
    public int Number { get; set; }
    public NodeAttemptStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ExecutionFailureCode? ErrorCode { get; set; }
    public ExternalOutcome ExternalOutcome { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int? HttpStatusCode { get; set; }
    public int? CredentialRevisionUsed { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public bool Legacy { get; set; }
}
internal sealed class ExecutionRedriveRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid MessageId { get; set; }
    public int ExpectedPublishAttempts { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
}

internal sealed class ExecutionReplayRecord
{
    public Guid Id { get; set; }
    public Guid OriginalExecutionId { get; set; }
    public Guid NewExecutionId { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string KeyDigest { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
