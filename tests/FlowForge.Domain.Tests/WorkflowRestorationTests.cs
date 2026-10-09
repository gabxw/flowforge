using System.Collections;
using FlowForge.Domain.Workflows;
using Xunit;
using static FlowForge.Domain.Tests.GraphFixtures;

namespace FlowForge.Domain.Tests;

public sealed class WorkflowRestorationTests
{
    private static readonly DateTimeOffset Start = new(639007092001234567, TimeSpan.FromHours(-3));

    [Fact]
    public void Restore_preserves_history_identity_revisions_order_and_utc_ticks()
    {
        var restored = Workflow.Restore(History());
        Assert.Equal(Id(4000), restored.Id);
        Assert.Equal(OwnerId, restored.OwnerUserId);
        Assert.Equal("Orders", restored.Name);
        Assert.Equal("History", restored.Description);
        Assert.Equal(37, restored.Revision);
        Assert.Equal(639007200001234567, restored.CreatedAt.Ticks);
        Assert.Equal(639007200001234666, restored.UpdatedAt.Ticks);
        Assert.Equal(TimeSpan.Zero, restored.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, restored.UpdatedAt.Offset);
        Assert.Null(restored.ArchivedAt);
        Assert.Equal(Id(1002), restored.CurrentPublishedVersionId);
        Assert.Equal(new[] { Id(1001), Id(1002), Id(1003) }, restored.Versions.Select(v => v.Id));
        Assert.Equal(new[] { 1, 2, 3 }, restored.Versions.Select(v => v.VersionNumber));
        Assert.Equal(new[] { 11, 17, 4 }, restored.Versions.Select(v => v.Revision));
        Assert.Equal(new[] { WorkflowVersionStatus.Published, WorkflowVersionStatus.Published, WorkflowVersionStatus.Draft },
            restored.Versions.Select(v => v.Status));
        Assert.Equal(639007200001234587, restored.Versions[0].PublishedAt!.Value.Ticks);
        Assert.Equal(639007200001234607, restored.Versions[1].PublishedAt!.Value.Ticks);
        Assert.Equal(639007200001234617, restored.DraftVersion!.CreatedAt.Ticks);
        Assert.Same(restored.Versions[2], restored.DraftVersion);
        Assert.All(restored.Versions, v =>
        {
            Assert.Equal(Id(4000), v.WorkflowId);
            Assert.Equal(OwnerId, v.OwnerUserId);
            Assert.Equal(TimeSpan.Zero, v.CreatedAt.Offset);
        });
        Assert.Equal(new[] { Id(1), Id(2) }, restored.Versions[0].Nodes.Select(n => n.NodeId));
        Assert.Equal(Id(2001), Assert.Single(restored.Versions[0].Connections).Id);
        Assert.Equal(Id(2002), Assert.Single(restored.Versions[1].Connections).Id);
        Assert.Empty(restored.DraftVersion.Connections);
        Assert.Equal(NodeType.Log, Assert.Single(restored.DraftVersion.Nodes).Type);
    }

    [Fact]
    public void Restored_active_draft_can_be_edited_using_preserved_counters()
    {
        var restored = Workflow.Restore(History());
        restored.ReplaceDraftGraph([], [], Start.AddTicks(100));
        Assert.Equal(38, restored.Revision);
        Assert.Equal(5, restored.DraftVersion!.Revision);
        Assert.Equal(639007200001234667, restored.UpdatedAt.Ticks);
    }

    [Fact]
    public void Restore_preserves_terminal_archive()
    {
        var restored = Workflow.Restore(History() with { ArchivedAt = Start.AddTicks(99) });
        Assert.Equal(639007200001234666, restored.ArchivedAt!.Value.Ticks);
        Assert.Equal(TimeSpan.Zero, restored.ArchivedAt.Value.Offset);
        Assert.Throws<InvalidOperationException>(() => restored.UpdateDetails("Changed", null, Start.AddTicks(100)));
    }

    [Fact]
    public void Restore_copies_every_collection_and_each_aggregate_is_independent()
    {
        var snapshot = History();
        var nodeList = snapshot.Versions[0].Nodes.ToList();
        var edges = snapshot.Versions[0].Connections.ToList();
        var versions = snapshot.Versions.ToList();
        versions[0] = versions[0] with { Nodes = nodeList, Connections = edges };
        snapshot = snapshot with { Versions = versions };
        var first = Workflow.Restore(snapshot);
        var second = Workflow.Restore(snapshot);
        nodeList.Clear();
        edges.Clear();
        versions.Clear();
        Assert.Equal(3, first.Versions.Count);
        Assert.Equal(2, first.Versions[0].Nodes.Count);
        Assert.Single(first.Versions[0].Connections);
        Assert.Throws<NotSupportedException>(() => ((IList)first.Versions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)first.Versions[0].Nodes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList)first.Versions[0].Connections).Clear());
        first.ReplaceDraftGraph([], [], Start.AddTicks(100));
        Assert.Single(second.DraftVersion!.Nodes);
        Assert.Equal(37, second.Revision);
    }

    [Fact]
    public void Restore_accepts_no_versions_and_a_lone_incomplete_draft()
    {
        var empty = History() with { Versions = [], CurrentPublishedVersionId = null };
        Assert.Empty(Workflow.Restore(empty).Versions);
        var draft = empty with { Versions = [History().Versions[2] with { VersionNumber = 1 }] };
        Assert.NotNull(Workflow.Restore(draft).DraftVersion);
    }

    [Fact]
    public void Restore_accepts_history_without_draft()
    {
        var snapshot = History();
        var restored = Workflow.Restore(snapshot with { Versions = snapshot.Versions.Take(2).ToArray() });
        Assert.Null(restored.DraftVersion);
        Assert.Equal(Id(1002), restored.CurrentPublishedVersionId);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("workflow-id")]
    [InlineData("owner-id")]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("revision")]
    [InlineData("versions-null")]
    [InlineData("version-null")]
    [InlineData("version-id")]
    [InlineData("duplicate-version")]
    [InlineData("version-revision")]
    [InlineData("version-zero")]
    [InlineData("version-gap")]
    [InlineData("status")]
    [InlineData("two-drafts")]
    [InlineData("draft-not-last")]
    [InlineData("draft-published-at")]
    [InlineData("published-no-date")]
    [InlineData("updated-before-created")]
    [InlineData("archive-not-updated")]
    [InlineData("version-before-created")]
    [InlineData("version-after-updated")]
    [InlineData("published-before-version")]
    [InlineData("published-after-updated")]
    [InlineData("pointer-missing")]
    [InlineData("pointer-old")]
    [InlineData("pointer-draft")]
    [InlineData("pointer-unknown")]
    [InlineData("pointer-without-publication")]
    [InlineData("nodes-null")]
    [InlineData("connections-null")]
    [InlineData("node-null")]
    [InlineData("connection-null")]
    [InlineData("duplicate-node")]
    [InlineData("duplicate-connection")]
    [InlineData("draft-node-version")]
    [InlineData("draft-connection-version")]
    [InlineData("invalid-published-graph")]
    public void Restore_rejects_corrupt_snapshots(string corruption)
    {
        var s = History();
        var versions = s.Versions.ToArray();
        s = s with { Versions = versions };
        switch (corruption)
        {
            case "null": s = null!; break;
            case "workflow-id": s = s with { Id = Guid.Empty }; break;
            case "owner-id": s = s with { OwnerUserId = Guid.Empty }; break;
            case "name": s = s with { Name = " " }; break;
            case "description": s = s with { Description = new string('x', 2001) }; break;
            case "revision": s = s with { Revision = -1 }; break;
            case "versions-null": s = s with { Versions = null! }; break;
            case "version-null": versions[0] = null!; break;
            case "version-id": versions[2] = versions[2] with { Id = Guid.Empty }; break;
            case "duplicate-version": versions[2] = versions[2] with { Id = versions[0].Id }; break;
            case "version-revision": versions[2] = versions[2] with { Revision = -1 }; break;
            case "version-zero": versions[0] = versions[0] with { VersionNumber = 0 }; break;
            case "version-gap": versions[1] = versions[1] with { VersionNumber = 4 }; break;
            case "status": versions[2] = versions[2] with { Status = (WorkflowVersionStatus)99 }; break;
            case "two-drafts": versions[1] = versions[1] with { Status = WorkflowVersionStatus.Draft, PublishedAt = null }; break;
            case "draft-not-last": versions[0] = versions[2] with { VersionNumber = 1 }; versions[2] = versions[1] with { Id = Id(999), VersionNumber = 3, Nodes = [Trigger(versionId: Id(999))], Connections = [] }; break;
            case "draft-published-at": versions[2] = versions[2] with { PublishedAt = Start.AddTicks(55) }; break;
            case "published-no-date": versions[0] = versions[0] with { PublishedAt = null }; break;
            case "updated-before-created": s = s with { UpdatedAt = Start.AddTicks(-1) }; break;
            case "archive-not-updated": s = s with { ArchivedAt = Start.AddTicks(98) }; break;
            case "version-before-created": versions[0] = versions[0] with { CreatedAt = Start.AddTicks(-1) }; break;
            case "version-after-updated": versions[2] = versions[2] with { CreatedAt = Start.AddTicks(100) }; break;
            case "published-before-version": versions[0] = versions[0] with { PublishedAt = Start.AddTicks(5) }; break;
            case "published-after-updated": versions[1] = versions[1] with { PublishedAt = Start.AddTicks(100) }; break;
            case "pointer-missing": s = s with { CurrentPublishedVersionId = null }; break;
            case "pointer-old": s = s with { CurrentPublishedVersionId = Id(1001) }; break;
            case "pointer-draft": s = s with { CurrentPublishedVersionId = Id(1003) }; break;
            case "pointer-unknown": s = s with { CurrentPublishedVersionId = Id(5000) }; break;
            case "pointer-without-publication": s = s with { Versions = [] }; break;
            case "nodes-null": versions[2] = versions[2] with { Nodes = null! }; break;
            case "connections-null": versions[2] = versions[2] with { Connections = null! }; break;
            case "node-null": versions[2] = versions[2] with { Nodes = [null!] }; break;
            case "connection-null": versions[2] = versions[2] with { Connections = [null!] }; break;
            case "duplicate-node": versions[2] = versions[2] with { Nodes = [Log(versionId: Id(1003)), Log(versionId: Id(1003))] }; break;
            case "duplicate-connection": versions[2] = versions[2] with { Connections = [Edge(2001, 1, 2, versionId: Id(1003))] }; break;
            case "draft-node-version": versions[2] = versions[2] with { Nodes = [Log()] }; break;
            case "draft-connection-version": versions[2] = versions[2] with { Connections = [Edge(3000, 1, 2)] }; break;
            case "invalid-published-graph": versions[0] = versions[0] with { Nodes = [], Connections = [] }; break;
            default: throw new InvalidOperationException(corruption);
        }
        Assert.ThrowsAny<ArgumentException>(() => Workflow.Restore(s));
    }

    private static WorkflowSnapshot History() => new(Id(4000), OwnerId, "Orders", "History", Start,
        Start.AddTicks(99), null, 37, Id(1002),
        [
            new(Id(1001), 1, WorkflowVersionStatus.Published, Start.AddTicks(10), Start.AddTicks(20), 11,
                [Trigger(versionId: Id(1001)), Log(versionId: Id(1001))], [Edge(2001, 1, 2, versionId: Id(1001))]),
            new(Id(1002), 2, WorkflowVersionStatus.Published, Start.AddTicks(30), Start.AddTicks(40), 17,
                [Trigger(versionId: Id(1002)), Log(versionId: Id(1002))], [Edge(2002, 1, 2, versionId: Id(1002))]),
            new(Id(1003), 3, WorkflowVersionStatus.Draft, Start.AddTicks(50), null, 4,
                [Log(versionId: Id(1003))], [])
        ]);
}
