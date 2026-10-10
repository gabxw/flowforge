using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;

namespace FlowForge.IntegrationTests;
internal static class EngineFixtures
{
    internal static async Task<(Workflow Workflow, ExecutionRequest Request, WorkflowExecutionSnapshot Execution)> RequestAsync(
        DispatchFixture fixture, NodeType middle = NodeType.Log, bool tail = false)
    {
        var owner = Guid.NewGuid(); var now = WorkflowStoreFixtures.Start;
        await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, now);
        var workflow = new Workflow(Guid.NewGuid(), owner, "Engine", now);
        var version = workflow.CreateDraft(Guid.NewGuid(), now);
        var trigger = Guid.NewGuid(); var node = Guid.NewGuid(); var last = Guid.NewGuid();
        NodeConfiguration config = middle switch
        {
            NodeType.Log => new LogConfiguration("mensagem-ficticia-confidencial"),
            NodeType.HttpRequest => new HttpRequestConfiguration(new Uri("https://example.com"), HttpRequestMethod.Get),
            NodeType.Delay => new DelayConfiguration(TimeSpan.FromMinutes(1)),
            _ => throw new ArgumentException("Tipo inválido para fixture.")
        };
        var nodes = new List<WorkflowNode> { new(version.Id, trigger, NodeType.WebhookTrigger, new WebhookTriggerConfiguration()),
            new(version.Id, node, middle, config) };
        var edges = new List<WorkflowConnection> { new(Guid.NewGuid(), version.Id, trigger, node, "next") };
        if (tail) { nodes.Add(new(version.Id, last, NodeType.Log, new LogConfiguration("Fim"))); edges.Add(new(Guid.NewGuid(), version.Id, node, last, "next")); }
        workflow.ReplaceDraftGraph(nodes, edges, now); workflow.PublishDraft(now);
        await new PostgresWorkflowStore(fixture.Factory).AddAsync(workflow);
        var request = new ExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflow.Id, owner);
        var execution = await new PostgresExecutionStore(fixture.Factory).RequestAsync(request);
        return (workflow, request, execution);
    }
}
