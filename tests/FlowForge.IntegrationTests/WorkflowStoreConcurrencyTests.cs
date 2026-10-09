using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;
namespace FlowForge.IntegrationTests;
[Collection("PostgreSQL")]
public sealed class WorkflowStoreConcurrencyTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Independent_clients_compete_for_same_revision_and_only_winner_is_committed()
    {
        var owner = Guid.NewGuid(); var original = await CreateAsync(fixture, owner);
        var firstStore = new PostgresWorkflowStore(fixture.Factory); var secondStore = new PostgresWorkflowStore(fixture.Factory);
        var first = (await firstStore.GetAsync(original.Id, owner))!; var second = (await secondStore.GetAsync(original.Id, owner))!;
        var revision = first.Revision;
        first.UpdateDetails("First writer", "First", Start.AddSeconds(1)); second.UpdateDetails("Second writer", "Second", Start.AddSeconds(2));
        async Task<Exception?> Attempt(PostgresWorkflowStore store, Workflow workflow) { try { await store.SaveAsync(workflow, revision); return null; } catch (Exception exception) { return exception; } }
        var outcomes = await Task.WhenAll(Attempt(firstStore, first), Attempt(secondStore, second));
        Assert.Single(outcomes, e => e is null); Assert.IsType<WorkflowConcurrencyException>(Assert.Single(outcomes, e => e is not null));
        var winner = outcomes[0] is null ? first : second;
        var actual = (await new PostgresWorkflowStore(fixture.Factory).GetAsync(original.Id, owner))!;
        Assert.Equal(winner.Name, actual.Name); Assert.Equal(winner.Description, actual.Description); Assert.Equal(revision + 1, actual.Revision);
        Assert.Equal(winner.CurrentPublishedVersionId, actual.CurrentPublishedVersionId);
        Assert.True(WorkflowPersistenceMapper.SameGraph(WorkflowPersistenceMapper.Capture(winner).Versions[1], WorkflowPersistenceMapper.Capture(actual).Versions[1]));
    }

    [Fact]
    public async Task Foreign_key_failure_after_cas_rolls_back_metadata_graph_pointer_and_revision()
    {
        var owner = Guid.NewGuid(); var original = await CreateAsync(fixture, owner);
        var store = new PostgresWorkflowStore(fixture.Factory); var edit = (await store.GetAsync(original.Id, owner))!;
        var revision = edit.Revision; var draft = edit.DraftVersion!; var node = draft.Nodes[0];
        edit.ReplaceDraftGraph([node], [new WorkflowConnection(Guid.NewGuid(), draft.Id, node.NodeId, Guid.NewGuid(), "next")], Start.AddSeconds(1));
        edit.UpdateDetails("Must rollback", "Changed", Start.AddSeconds(2));
        await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveAsync(edit, revision));
        var actual = (await new PostgresWorkflowStore(fixture.Factory).GetAsync(original.Id, owner))!;
        Assert.Equal(original.Name, actual.Name); Assert.Equal(original.Description, actual.Description); Assert.Equal(revision, actual.Revision);
        Assert.Equal(original.CurrentPublishedVersionId, actual.CurrentPublishedVersionId);
        Assert.True(WorkflowPersistenceMapper.SameGraph(WorkflowPersistenceMapper.Capture(original).Versions[1], WorkflowPersistenceMapper.Capture(actual).Versions[1]));
        actual.UpdateDetails("Recovered", null, Start.AddSeconds(3));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(actual, revision);
        Assert.Equal("Recovered", (await store.GetAsync(original.Id, owner))!.Name);
    }
}
