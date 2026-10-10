using FlowForge.Domain.Workflows;

namespace FlowForge.Domain.Executions;

// Seleciona uma porta declarada. Executores não recebem autoridade para escolher um node arbitrário.
public sealed class ExecutionPath
{
    private readonly WorkflowVersionSnapshot version;
    public ExecutionPath(WorkflowVersionSnapshot version, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Status != WorkflowVersionStatus.Published ||
            WorkflowGraphValidator.Validate(version.Id, owner, version.Nodes, version.Connections).Count != 0)
            throw new ArgumentException("Execução exige uma versão publicada e um grafo válido.");
        this.version = version;
    }
    public Guid First => version.Nodes.Single(n => n.Type == NodeType.WebhookTrigger).NodeId;
    public WorkflowNode Node(Guid id) => version.Nodes.Single(n => n.NodeId == id);
    public Guid? Next(Guid nodeId, string port)
    {
        var node = Node(nodeId);
        if (node.Type == NodeType.Condition ? port is not ("true" or "false") : port != "next")
            throw new ArgumentException("Porta de execução incompatível.", nameof(port));
        return version.Connections.SingleOrDefault(c => c.SourceNodeId == nodeId && c.SourcePort == port)?.TargetNodeId;
    }
}
