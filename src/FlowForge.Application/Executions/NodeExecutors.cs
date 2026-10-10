using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;

// Entrada técnica do grafo; não implementa nem autentica o endpoint webhook da Fase 7.
public sealed class TriggerNodeExecutor : INodeExecutor
{
    public NodeType Type => NodeType.WebhookTrigger;
    public bool CanReplayAfterInterruption => true;
    public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NodeResult.Success(context.Input));
    }
}

public sealed class LogNodeExecutor : INodeExecutor
{
    public NodeType Type => NodeType.Log;
    public bool CanReplayAfterInterruption => true;
    public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // A mensagem é gravada protegida no checkpoint; nunca enviada ao ILogger.
        return Task.FromResult(NodeResult.Success(context.Input, logMessage: ((LogConfiguration)context.Node.Configuration).Message));
    }
}
