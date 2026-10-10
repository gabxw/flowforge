using FlowForge.Application.Credentials;
using FlowForge.Domain.Credentials;
using FlowForge.Application.Users;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using Xunit;

namespace FlowForge.Api.Tests;

public sealed class WorkflowCommandTests
{
    [Fact]
    public async Task Successful_command_returns_its_own_result_even_when_another_edit_commits_before_the_response()
    {
        var owner = Guid.NewGuid();
        var store = new InterleavingStore(owner);
        var service = new WorkflowService(store, new TechnicalUser(), TimeProvider.System, new CredentialService(new UnexpectedCredentialStore(), new TechnicalUser(), TimeProvider.System));
        var result = await service.UpdateAsync(owner, store.Id, 0, "Minha edição", null);
        Assert.Equal("Minha edição", result.Name);
        Assert.Equal(1, result.Revision);
        Assert.Equal("Edição seguinte", (await store.GetAsync(store.Id, owner))!.Name);
    }

    [Fact]
    public async Task Creation_has_microsecond_timestamps_and_returns_the_created_resource_without_a_second_read()
    {
        var store = new CreateOnlyStore();
        var service = new WorkflowService(store, new TechnicalUser(), TimeProvider.System, new CredentialService(new UnexpectedCredentialStore(), new TechnicalUser(), TimeProvider.System));
        var result = await service.CreateAsync(Guid.NewGuid(), "Novo", null);
        Assert.Same(store.Created, result);
        Assert.Equal(0, result.CreatedAt.UtcTicks % 10);
        Assert.Equal(result.CreatedAt, result.DraftVersion!.CreatedAt);
    }

    private sealed class UnexpectedCredentialStore : ICredentialStore
    {
        public Task CreateAsync(CredentialSnapshot metadata, CredentialSecret secret, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot?> GetAsync(Guid id, Guid owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CredentialSnapshot>> ListAsync(Guid owner, int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot> RotateAsync(Guid id, Guid owner, int expectedRevision, CredentialSecret secret, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialSnapshot> RevokeAsync(Guid id, Guid owner, int expectedRevision, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedCredential?> ResolveAsync(Guid id, Guid owner, HttpsOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TechnicalUser : ITechnicalUserStore
    {
        public Task EnsureExistsAsync(Guid id, DateTimeOffset createdAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InterleavingStore(Guid owner) : IWorkflowStore
    {
        public Guid Id { get; } = Guid.NewGuid();
        private Workflow? next;
        public Task<Workflow?> GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Workflow?>(next ?? new Workflow(Id, owner, "Original", DateTimeOffset.UnixEpoch));
        public Task SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default)
        {
            next = Workflow.Restore(new WorkflowSnapshot(workflow.Id, workflow.OwnerUserId, workflow.Name, workflow.Description,
                workflow.CreatedAt, workflow.UpdatedAt, workflow.ArchivedAt, workflow.Revision, workflow.CurrentPublishedVersionId, []));
            next.UpdateDetails("Edição seguinte", null, workflow.UpdatedAt);
            return Task.CompletedTask;
        }
        public Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid ownerUserId, int offset = 0, int limit = 20, bool includeArchived = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CreateOnlyStore : IWorkflowStore
    {
        public Workflow? Created { get; private set; }
        public Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default)
        {
            Created = workflow;
            return Task.CompletedTask;
        }
        public Task<Workflow?> GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A resposta não deve depender de uma leitura depois do commit.");
        public Task SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid ownerUserId, int offset = 0, int limit = 20, bool includeArchived = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
