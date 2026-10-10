using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;

namespace FlowForge.Infrastructure.Persistence.Records;
internal sealed class NodeExecutionRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid WorkflowVersionId { get; set; }
    public Guid NodeId { get; set; }
    public int Ordinal { get; set; }
    public NodeExecutionStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? InputSummary { get; set; }
    public string? OutputSummary { get; set; }
    public ExecutionFailureCode? ErrorCode { get; set; }
}
internal sealed class ExecutionLogRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid NodeExecutionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ExecutionLogCode EventCode { get; set; }
    public byte[] MessageProtected { get; set; } = [];
    public int MessageByteLength { get; set; }
}
