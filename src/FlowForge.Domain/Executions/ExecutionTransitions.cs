namespace FlowForge.Domain.Executions;

public static class ExecutionTransitions
{
    public static bool CanTransition(WorkflowExecutionStatus from, WorkflowExecutionStatus to)
        => (from, to) switch
        {
            (WorkflowExecutionStatus.Pending, WorkflowExecutionStatus.Running) => true,
            (WorkflowExecutionStatus.Pending, WorkflowExecutionStatus.Cancelled) => true,
            (WorkflowExecutionStatus.Running, WorkflowExecutionStatus.Succeeded) => true,
            (WorkflowExecutionStatus.Running, WorkflowExecutionStatus.Failed) => true,
            (WorkflowExecutionStatus.Running, WorkflowExecutionStatus.Cancelled) => true,
            _ => false
        };

    public static bool CanTransition(NodeExecutionStatus from, NodeExecutionStatus to)
        => (from, to) switch
        {
            (NodeExecutionStatus.Pending, NodeExecutionStatus.Running) => true,
            (NodeExecutionStatus.Pending, NodeExecutionStatus.Skipped) => true,
            (NodeExecutionStatus.Running, NodeExecutionStatus.Succeeded) => true,
            (NodeExecutionStatus.Running, NodeExecutionStatus.Failed) => true,
            (NodeExecutionStatus.Running, NodeExecutionStatus.Retrying) => true,
            (NodeExecutionStatus.Running, NodeExecutionStatus.Cancelled) => true,
            (NodeExecutionStatus.Retrying, NodeExecutionStatus.Running) => true,
            (NodeExecutionStatus.Retrying, NodeExecutionStatus.Cancelled) => true,
            _ => false
        };

    public static void EnsureTransition(WorkflowExecutionStatus from, WorkflowExecutionStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Workflow execution cannot transition from {from} to {to}.");
    }

    public static void EnsureTransition(NodeExecutionStatus from, NodeExecutionStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Node execution cannot transition from {from} to {to}.");
    }
}
