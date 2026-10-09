using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Domain.Workflows;

public sealed class WorkflowNode
{
    public WorkflowNode(Guid workflowVersionId, Guid nodeId, NodeType type, NodeConfiguration configuration,
        NodePosition position = default, CredentialReference? credential = null)
    {
        if (workflowVersionId == Guid.Empty)
            throw new ArgumentException("A versão deve ter uma identidade.", nameof(workflowVersionId));
        if (nodeId == Guid.Empty)
            throw new ArgumentException("O node deve ter uma identidade.", nameof(nodeId));
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type), "Tipo de node desconhecido.");
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Type != type)
            throw new ArgumentException("A configuração deve corresponder ao tipo do node.", nameof(configuration));
        if (credential is not null && type != NodeType.HttpRequest)
            throw new ArgumentException("Somente HTTP Request aceita credencial.", nameof(credential));

        WorkflowVersionId = workflowVersionId;
        NodeId = nodeId;
        Type = type;
        Configuration = configuration;
        Position = position;
        Credential = credential;
    }
    public Guid WorkflowVersionId { get; }
    public Guid NodeId { get; }
    public NodeType Type { get; }
    public NodeConfiguration Configuration { get; }
    public NodePosition Position { get; }
    public CredentialReference? Credential { get; }
}
