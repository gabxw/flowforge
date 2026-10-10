using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

namespace FlowForge.Application.Executions;

// Retorna intenção durável. O store grava prazo/outbox e a engine libera o delivery.
public sealed class DelayNodeExecutor : INodeExecutor
{
    public NodeType Type => NodeType.Delay;
    public bool CanReplayAfterInterruption => true;
    public Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(NodeResult.Suspend(context.Input, ((DelayConfiguration)context.Node.Configuration).Duration));
    }
}
