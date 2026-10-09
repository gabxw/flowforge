using FlowForge.Domain.Workflows;
namespace FlowForge.Application.Workflows;
public interface IWorkflowStore
{
    Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default);
    Task<Workflow?> GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default);
    Task SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid ownerUserId, int offset = 0, int limit = 20,
        bool includeArchived = false, CancellationToken cancellationToken = default);
}
