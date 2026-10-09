namespace FlowForge.Domain.Executions;

public enum NodeExecutionStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Retrying = 5,
    Skipped = 6,
    Cancelled = 7
}
