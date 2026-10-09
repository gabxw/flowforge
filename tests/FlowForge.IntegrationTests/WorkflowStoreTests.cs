using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;
namespace FlowForge.IntegrationTests;
[Collection("PostgreSQL")]
public sealed class WorkflowStoreTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Round_trip_preserves_versions_configuration_order_ids_and_microseconds()
    {
        var owner = Guid.NewGuid();
        var original = await CreateAsync(fixture, owner);
        var restored = Assert.IsType<Workflow>(await new PostgresWorkflowStore(fixture.Factory).GetAsync(original.Id, owner));
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Revision, restored.Revision);
        Assert.Equal(WorkflowPersistenceMapper.Normalize(original.CreatedAt), restored.CreatedAt);
        Assert.Equal(original.CurrentPublishedVersionId, restored.CurrentPublishedVersionId);
        Assert.Equal(2, restored.Versions.Count);
        for (var i = 0; i < original.Versions.Count; i++)
        {
            var expected = WorkflowPersistenceMapper.Capture(original).Versions[i];
            var actual = WorkflowPersistenceMapper.Capture(restored).Versions[i];
            Assert.Equal(expected.Revision, actual.Revision);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.PublishedAt, actual.PublishedAt);
            Assert.Equal(expected.Nodes.Select(n => n.NodeId), actual.Nodes.Select(n => n.NodeId));
            Assert.Equal(expected.Connections.Select(c => c.Id), actual.Connections.Select(c => c.Id));
            Assert.True(WorkflowPersistenceMapper.SameGraph(expected, actual));
        }
        var nodes = restored.Versions[0].Nodes;
        Assert.Equal(123456789, Assert.IsType<DelayConfiguration>(nodes.Single(n => n.Type == NodeType.Delay).Configuration).Duration.Ticks);
        Assert.Equal(123456.7890123456789m, Assert.IsType<ConditionConfiguration>(nodes.Single(n => n.Type == NodeType.Condition).Configuration).ExpectedValue!.Value.GetDecimal());
        Assert.Equal(owner, nodes.Single(n => n.Type == NodeType.HttpRequest).Credential!.OwnerUserId);
    }

    [Fact]
    public async Task Add_then_save_same_aggregate_handles_timestamp_precision_and_jsonb_normalization()
    {
        var owner = Guid.NewGuid(); var workflow = await CreateAsync(fixture, owner);
        var revision = workflow.Revision;
        workflow.UpdateDetails("Updated", "Description", Start.AddTicks(57));
        await new PostgresWorkflowStore(fixture.Factory).SaveAsync(workflow, revision);
        var actual = (await new PostgresWorkflowStore(fixture.Factory).GetAsync(workflow.Id, owner))!;
        Assert.Equal("Updated", actual.Name);
        Assert.Equal(WorkflowPersistenceMapper.Normalize(Start.AddTicks(57)), actual.UpdatedAt);
        Assert.Equal(revision + 1, actual.Revision);
        Assert.Equal(2, actual.Versions.Count);
    }

    [Fact]
    public async Task Publish_and_open_another_draft_in_one_save_preserves_previous_publication()
    {
        var owner = Guid.NewGuid(); var original = await CreateAsync(fixture, owner);
        var store = new PostgresWorkflowStore(fixture.Factory);
        var workflow = (await store.GetAsync(original.Id, owner))!;
        var expected = workflow.Revision;
        workflow.PublishDraft(Start.AddTicks(60));
        workflow.CreateDraft(Guid.NewGuid(), Start.AddTicks(70));
        await store.SaveAsync(workflow, expected);
        var restored = (await store.GetAsync(original.Id, owner))!;
        Assert.Equal(new[] { WorkflowVersionStatus.Published, WorkflowVersionStatus.Published, WorkflowVersionStatus.Draft }, restored.Versions.Select(v => v.Status));
        Assert.Equal(restored.Versions[1].Id, restored.CurrentPublishedVersionId);
        Assert.Equal(restored.Versions[2].Id, restored.DraftVersion!.Id);
        Assert.True(WorkflowPersistenceMapper.SameGraph(WorkflowPersistenceMapper.Capture(original).Versions[0], WorkflowPersistenceMapper.Capture(restored).Versions[0]));
    }

    [Fact]
    public async Task Scope_pagination_and_archive_do_not_leak_or_load_other_owners()
    {
        var owner = Guid.NewGuid(); var otherOwner = Guid.NewGuid();
        var first = await CreateAsync(fixture, owner, "First");
        var second = await CreateAsync(fixture, owner, "Second");
        var third = await CreateAsync(fixture, owner, "Third");
        var other = await CreateAsync(fixture, otherOwner, "Other");
        var store = new PostgresWorkflowStore(fixture.Factory);
        var secondRevision = second.Revision; second.UpdateDetails("Second", null, Start.AddMinutes(1)); await store.SaveAsync(second, secondRevision);
        var thirdRevision = third.Revision; third.UpdateDetails("Third", null, Start.AddMinutes(2)); await store.SaveAsync(third, thirdRevision);
        Assert.Equal(third.Id, Assert.Single(await store.ListAsync(owner, limit: 1)).Id);
        Assert.Equal(second.Id, Assert.Single(await store.ListAsync(owner, offset: 1, limit: 1)).Id);
        Assert.Null(await store.GetAsync(other.Id, owner));
        var wrongOwner = Workflow.Restore(WorkflowPersistenceMapper.Capture(first) with { OwnerUserId = otherOwner, Versions = [] , CurrentPublishedVersionId = null });
        wrongOwner.UpdateDetails("Forbidden", null, Start.AddMinutes(3));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveAsync(wrongOwner, first.Revision));
        var previous = third.Revision; third.Archive(Start.AddMinutes(3)); await store.SaveAsync(third, previous);
        Assert.Equal(2, (await store.ListAsync(owner)).Count);
        Assert.Equal(3, (await store.ListAsync(owner, includeArchived: true)).Count);
        Assert.NotNull((await store.GetAsync(third.Id, owner))!.ArchivedAt);
        Assert.Single(await store.ListAsync(otherOwner));
    }

    [Fact]
    public async Task Incomplete_draft_is_persistable_and_missing_owner_add_rolls_back()
    {
        var owner = Guid.NewGuid(); await new PostgresTechnicalUserStore(fixture.Factory).EnsureExistsAsync(owner, Start);
        var workflow = new Workflow(Guid.NewGuid(), owner, "Incomplete", Start);
        workflow.CreateDraft(Guid.NewGuid(), Start);
        var store = new PostgresWorkflowStore(fixture.Factory); await store.AddAsync(workflow);
        Assert.Empty((await store.GetAsync(workflow.Id, owner))!.DraftVersion!.Nodes);
        var missingOwner = Definition(Guid.NewGuid());
        await Assert.ThrowsAsync<DbUpdateException>(() => store.AddAsync(missingOwner));
        Assert.Null(await store.GetAsync(missingOwner.Id, missingOwner.OwnerUserId));
    }

    [Fact]
    public async Task Historical_publication_cannot_be_replaced_through_restored_snapshot()
    {
        var owner = Guid.NewGuid(); var original = await CreateAsync(fixture, owner);
        var snapshot = WorkflowPersistenceMapper.Capture(original); var versions = snapshot.Versions.ToArray();
        var nodes = versions[0].Nodes.Select(n => n.Type == NodeType.Log
            ? new WorkflowNode(n.WorkflowVersionId, n.NodeId, n.Type, new LogConfiguration("Tampered"), n.Position) : n).ToArray();
        versions[0] = versions[0] with { Nodes = nodes };
        var tampered = Workflow.Restore(snapshot with { Name = "Tampered", Revision = snapshot.Revision + 1, Versions = versions });
        var store = new PostgresWorkflowStore(fixture.Factory);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(tampered, original.Revision));
        var actual = (await store.GetAsync(original.Id, owner))!;
        Assert.Equal(original.Name, actual.Name); Assert.Equal(original.Revision, actual.Revision);
        Assert.True(WorkflowPersistenceMapper.SameGraph(snapshot.Versions[0], WorkflowPersistenceMapper.Capture(actual).Versions[0]));
    }

    [Fact]
    public async Task Cancelled_operation_does_not_change_persisted_state()
    {
        var owner = Guid.NewGuid(); var workflow = await CreateAsync(fixture, owner);
        var revision = workflow.Revision; workflow.UpdateDetails("Cancelled", null, Start.AddSeconds(1));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var store = new PostgresWorkflowStore(fixture.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(workflow, revision, cancellation.Token));
        Assert.Equal(revision, (await store.GetAsync(workflow.Id, owner))!.Revision);
    }
    [Fact]
    public async Task Archived_workflow_cannot_be_reactivated_through_a_restored_snapshot()
    {
        var owner = Guid.NewGuid(); var workflow = await CreateAsync(fixture, owner);
        var store = new PostgresWorkflowStore(fixture.Factory);
        var revision = workflow.Revision; workflow.Archive(Start.AddMinutes(1));
        await store.SaveAsync(workflow, revision);
        var snapshot = WorkflowPersistenceMapper.Capture(workflow);
        var reactivated = Workflow.Restore(snapshot with
        {
            ArchivedAt = null, UpdatedAt = Start.AddMinutes(2), Revision = snapshot.Revision + 1
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(reactivated, snapshot.Revision));
        var actual = (await store.GetAsync(workflow.Id, owner))!;
        Assert.Equal(snapshot.Revision, actual.Revision);
        Assert.Equal(snapshot.ArchivedAt, actual.ArchivedAt);
        Assert.Equal(snapshot.UpdatedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task Invalid_identity_revision_and_pagination_are_rejected()
    {
        var owner = Guid.NewGuid(); var workflow = await CreateAsync(fixture, owner);
        var store = new PostgresWorkflowStore(fixture.Factory);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(Guid.Empty, owner));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(workflow.Id, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(owner, offset: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(owner, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListAsync(owner, limit: 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(workflow, -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveAsync(workflow, workflow.Revision));
        Assert.Equal(workflow.Revision, (await store.GetAsync(workflow.Id, owner))!.Revision);
    }
}
