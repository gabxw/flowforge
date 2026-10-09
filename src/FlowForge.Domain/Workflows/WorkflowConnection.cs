namespace FlowForge.Domain.Workflows;

public sealed class WorkflowConnection
{
    public WorkflowConnection(Guid id, Guid workflowVersionId, Guid sourceNodeId, Guid targetNodeId, string sourcePort)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A conexão deve ter uma identidade.", nameof(id));
        if (workflowVersionId == Guid.Empty)
            throw new ArgumentException("A versão deve ter uma identidade.", nameof(workflowVersionId));
        if (sourceNodeId == Guid.Empty)
            throw new ArgumentException("A origem deve ter uma identidade.", nameof(sourceNodeId));
        if (targetNodeId == Guid.Empty)
            throw new ArgumentException("O destino deve ter uma identidade.", nameof(targetNodeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePort);

        Id = id;
        WorkflowVersionId = workflowVersionId;
        SourceNodeId = sourceNodeId;
        TargetNodeId = targetNodeId;
        SourcePort = sourcePort;
    }
    public Guid Id { get; }
    public Guid WorkflowVersionId { get; }
    public Guid SourceNodeId { get; }
    public Guid TargetNodeId { get; }
    public string SourcePort { get; }
}
