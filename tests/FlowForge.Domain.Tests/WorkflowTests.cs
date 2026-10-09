using System.Collections;
using System.Text.Json;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;
using Xunit;
using static FlowForge.Domain.Tests.GraphFixtures;

namespace FlowForge.Domain.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void New_workflow_normalizes_name_and_time_and_starts_without_versions()
    {
        var workflow = NewWorkflow();
        Assert.Equal("Orders", workflow.Name);
        Assert.Equal(CreatedAt.ToUniversalTime(), workflow.CreatedAt);
        Assert.Equal(TimeSpan.Zero, workflow.CreatedAt.Offset);
        Assert.Equal(workflow.CreatedAt, workflow.UpdatedAt);
        Assert.Equal(0, workflow.Revision);
        Assert.Null(workflow.ArchivedAt);
        Assert.Null(workflow.DraftVersion);
        Assert.Null(workflow.CurrentPublishedVersionId);
        Assert.Empty(workflow.Versions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_rejects_missing_name(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Workflow(Id(1), OwnerId, name!, CreatedAt));
    }

    [Fact]
    public void Constructor_rejects_empty_ids_and_excessive_metadata()
    {
        Assert.Throws<ArgumentException>(() => new Workflow(Guid.Empty, OwnerId, "Orders", CreatedAt));
        Assert.Throws<ArgumentException>(() => new Workflow(Id(1), Guid.Empty, "Orders", CreatedAt));
        Assert.Throws<ArgumentException>(() => new Workflow(Id(1), OwnerId, new string('a', 201), CreatedAt));
        Assert.Throws<ArgumentException>(() => new Workflow(Id(1), OwnerId, "Orders", CreatedAt, new string('a', 2001)));
        var boundary = new Workflow(Id(1), OwnerId, new string('a', 200), CreatedAt, new string('b', 2000));
        Assert.Equal(200, boundary.Name.Length);
        Assert.Equal(2000, boundary.Description!.Length);
    }

    [Fact]
    public void First_draft_starts_empty_with_identity_and_number_owned_by_aggregate()
    {
        var workflow = NewWorkflow();
        var draft = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        Assert.Same(draft, workflow.DraftVersion);
        Assert.Same(draft, Assert.Single(workflow.Versions));
        Assert.Equal(workflow.Id, draft.WorkflowId);
        Assert.Equal(OwnerId, draft.OwnerUserId);
        Assert.Equal(1, draft.VersionNumber);
        Assert.Equal(WorkflowVersionStatus.Draft, draft.Status);
        Assert.Equal(0, draft.Revision);
        Assert.Equal(1, workflow.Revision);
        Assert.Equal(TimeSpan.Zero, draft.CreatedAt.Offset);
        Assert.Null(draft.PublishedAt);
        Assert.Empty(draft.Nodes);
        Assert.Empty(draft.Connections);
    }

    [Fact]
    public void Second_active_draft_and_empty_version_id_are_rejected_without_effects()
    {
        var workflow = NewWorkflow();
        var initial = Capture(workflow);
        Assert.Throws<ArgumentException>(() => workflow.CreateDraft(Guid.Empty, CreatedAt.AddMinutes(1)));
        Assert.Equal(initial, Capture(workflow));
        workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        var before = Capture(workflow);
        Assert.Throws<InvalidOperationException>(() => workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(2)));
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Version_ids_remain_unique_after_publication()
    {
        var workflow = NewWorkflow();
        PublishTrigger(workflow);
        var before = Capture(workflow);
        Assert.Throws<ArgumentException>(() => workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(4)));
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Draft_accepts_incomplete_graph_and_increments_both_revisions()
    {
        var workflow = NewWorkflow();
        var draft = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Log()], [], CreatedAt.AddMinutes(2));
        Assert.Equal(Id(2), Assert.Single(draft.Nodes).NodeId);
        Assert.Equal(1, draft.Revision);
        Assert.Equal(2, workflow.Revision);
        Assert.Equal(CreatedAt.AddMinutes(2).ToUniversalTime(), workflow.UpdatedAt);
    }

    [Fact]
    public void Successful_publication_returns_same_version_and_updates_pointer_revisions_and_utc_time()
    {
        var workflow = NewWorkflow();
        var draft = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Trigger()], [], CreatedAt.AddMinutes(2));
        var published = workflow.PublishDraft(CreatedAt.AddMinutes(3));
        Assert.Same(draft, published);
        Assert.Equal(WorkflowVersionStatus.Published, published.Status);
        Assert.Equal(VersionId, workflow.CurrentPublishedVersionId);
        Assert.Null(workflow.DraftVersion);
        Assert.Equal(CreatedAt.AddMinutes(3).ToUniversalTime(), published.PublishedAt);
        Assert.Equal(TimeSpan.Zero, published.PublishedAt!.Value.Offset);
        Assert.Equal(3, workflow.Revision);
        Assert.Equal(2, published.Revision);
    }

    [Fact]
    public void Invalid_publication_changes_no_state_and_exposes_readonly_errors()
    {
        var workflow = NewWorkflow();
        workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        var before = Capture(workflow);
        var exception = Assert.Throws<WorkflowValidationException>(() => workflow.PublishDraft(CreatedAt.AddMinutes(2)));
        Assert.Contains(exception.Errors, e => e.Code == GraphErrorCode.EmptyGraph);
        AssertReadonly(exception.Errors);
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Validation_exception_copies_supplied_errors()
    {
        var errors = new List<GraphValidationError> { new(GraphErrorCode.EmptyGraph, "Empty graph.") };
        var exception = new WorkflowValidationException(errors);
        errors.Clear();
        Assert.Equal(GraphErrorCode.EmptyGraph, Assert.Single(exception.Errors).Code);
        AssertReadonly(exception.Errors);
    }

    [Fact]
    public void Invalid_publication_preserves_previous_publication_and_pointer()
    {
        var workflow = NewWorkflow();
        var published = PublishTrigger(workflow);
        workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(4));
        workflow.ReplaceDraftGraph([], [], CreatedAt.AddMinutes(5));
        var before = Capture(workflow);
        Assert.Throws<WorkflowValidationException>(() => workflow.PublishDraft(CreatedAt.AddMinutes(6)));
        Assert.Equal(before, Capture(workflow));
        Assert.Equal(published.Id, workflow.CurrentPublishedVersionId);
        Assert.Equal(WorkflowVersionStatus.Published, published.Status);
    }

    [Fact]
    public void Publishing_rejects_node_connection_and_credential_scope_mismatches_atomically()
    {
        var workflow = NewWorkflow();
        workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Trigger(), Http(2, Id(8888), Id(7777))],
            [Edge(10, 1, 2, versionId: Id(6666))], CreatedAt.AddMinutes(2));
        var before = Capture(workflow);
        var exception = Assert.Throws<WorkflowValidationException>(() => workflow.PublishDraft(CreatedAt.AddMinutes(3)));
        Assert.Contains(exception.Errors, e => e.Code == GraphErrorCode.NodeVersionMismatch);
        Assert.Contains(exception.Errors, e => e.Code == GraphErrorCode.ConnectionVersionMismatch);
        Assert.Contains(exception.Errors, e => e.Code == GraphErrorCode.CredentialOwnerMismatch);
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void New_draft_clones_active_publication_and_reassigns_connection_ids_and_version_scopes()
    {
        var workflow = NewWorkflow();
        var first = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        var trigger = Trigger();
        var http = Http(2, OwnerId);
        var edge = Edge(10, 1, 2);
        workflow.ReplaceDraftGraph([trigger, http], [edge], CreatedAt.AddMinutes(2));
        workflow.PublishDraft(CreatedAt.AddMinutes(3));
        var second = workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(4));
        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(0, second.Revision);
        Assert.Equal(2, second.Nodes.Count);
        Assert.Equal(new[] { Id(1), Id(2) }, second.Nodes.Select(n => n.NodeId));
        Assert.All(second.Nodes, n => Assert.Equal(second.Id, n.WorkflowVersionId));
        Assert.Same(http.Configuration, second.Nodes[1].Configuration);
        Assert.Equal(http.Position, second.Nodes[1].Position);
        Assert.Equal(http.Credential, second.Nodes[1].Credential);
        var clonedEdge = Assert.Single(second.Connections);
        Assert.NotEqual(edge.Id, clonedEdge.Id);
        Assert.NotEqual(Guid.Empty, clonedEdge.Id);
        Assert.Equal(second.Id, clonedEdge.WorkflowVersionId);
        Assert.Equal(Id(1), clonedEdge.SourceNodeId);
        Assert.Equal(Id(2), clonedEdge.TargetNodeId);
        Assert.Equal("next", clonedEdge.SourcePort);
        workflow.ReplaceDraftGraph([Trigger(versionId: second.Id)], [], CreatedAt.AddMinutes(5));
        workflow.PublishDraft(CreatedAt.AddMinutes(6));
        Assert.Equal(second.Id, workflow.CurrentPublishedVersionId);
        Assert.Same(first, workflow.Versions[0]);
        Assert.Equal(WorkflowVersionStatus.Published, first.Status);
        Assert.Equal(2, first.Nodes.Count);
        Assert.Same(trigger, first.Nodes[0]);
        Assert.Same(edge, Assert.Single(first.Connections));
        Assert.Equal(CreatedAt.AddMinutes(3).ToUniversalTime(), first.PublishedAt);
        Assert.Equal(2, first.Revision);
    }

    [Fact]
    public void Caller_collections_and_casts_cannot_change_published_graph_or_version_history()
    {
        var workflow = NewWorkflow();
        var draft = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        var nodes = new List<WorkflowNode> { Trigger(), Log() };
        var connections = new List<WorkflowConnection> { Edge(10, 1, 2) };
        workflow.ReplaceDraftGraph(nodes, connections, CreatedAt.AddMinutes(2));
        nodes.Clear();
        connections.Clear();
        workflow.PublishDraft(CreatedAt.AddMinutes(3));
        Assert.Equal(2, draft.Nodes.Count);
        Assert.Single(draft.Connections);
        AssertReadonly(draft.Nodes);
        AssertReadonly(draft.Connections);
        AssertReadonly(workflow.Versions);
        Assert.Equal(2, draft.Nodes.Count);
        Assert.Single(draft.Connections);
        Assert.Single(workflow.Versions);
    }

    [Fact]
    public void Published_json_configuration_survives_disposed_document_and_new_draft()
    {
        var workflow = NewWorkflow();
        var draft = workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        TransformJsonConfiguration configuration;
        using (var document = JsonDocument.Parse("{\"nested\":[1,true,null]}"))
        {
            configuration = new TransformJsonConfiguration([TransformField.FromValue("result", document.RootElement)]);
            workflow.ReplaceDraftGraph([Trigger(), new WorkflowNode(VersionId, Id(2), NodeType.TransformJson, configuration)],
                [Edge(10, 1, 2)], CreatedAt.AddMinutes(2));
        }
        workflow.PublishDraft(CreatedAt.AddMinutes(3));
        var second = workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(4));
        Assert.Same(configuration, draft.Nodes[1].Configuration);
        Assert.Same(configuration, second.Nodes[1].Configuration);
        Assert.Equal("{\"nested\":[1,true,null]}", configuration.Fields[0].Literal!.Value.GetRawText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Throwing_graph_enumerables_leave_existing_graph_and_all_state_unchanged(bool nodesThrow)
    {
        var workflow = NewWorkflow();
        workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Trigger(), Log()], [Edge(10, 1, 2)], CreatedAt.AddMinutes(2));
        var before = Capture(workflow);
        var nodes = nodesThrow ? YieldThenThrow(Trigger()) : new[] { Trigger() };
        var edges = nodesThrow ? Array.Empty<WorkflowConnection>() : YieldThenThrow(Edge(11, 1, 2));
        Assert.Throws<InvalidOperationException>(() => workflow.ReplaceDraftGraph(nodes, edges, CreatedAt.AddMinutes(3)));
        Assert.Equal(before, Capture(workflow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Null_graph_arguments_and_elements_are_atomic(int scenario)
    {
        var workflow = NewWorkflow();
        workflow.CreateDraft(VersionId, CreatedAt.AddMinutes(1));
        workflow.ReplaceDraftGraph([Trigger()], [], CreatedAt.AddMinutes(2));
        var before = Capture(workflow);
        IEnumerable<WorkflowNode> nodes = scenario == 0 ? null! : scenario == 2 ? [null!] : [Trigger()];
        IEnumerable<WorkflowConnection> edges = scenario == 1 ? null! : scenario == 3 ? [null!] : [];
        Assert.ThrowsAny<ArgumentException>(() => workflow.ReplaceDraftGraph(nodes, edges, CreatedAt.AddMinutes(3)));
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Metadata_edit_normalizes_name_updates_utc_and_keeps_versions_unchanged()
    {
        var workflow = NewWorkflow();
        var published = PublishTrigger(workflow);
        workflow.UpdateDetails(" Updated ", null, CreatedAt.AddMinutes(4));
        Assert.Equal("Updated", workflow.Name);
        Assert.Null(workflow.Description);
        Assert.Equal(4, workflow.Revision);
        Assert.Equal(CreatedAt.AddMinutes(4).ToUniversalTime(), workflow.UpdatedAt);
        Assert.Equal(2, published.Revision);
        Assert.Equal(published.Id, workflow.CurrentPublishedVersionId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Invalid_metadata_edit_has_no_effect(int scenario)
    {
        var workflow = NewWorkflow();
        PublishTrigger(workflow);
        var before = Capture(workflow);
        var name = scenario switch { 0 => null!, 1 => "  ", 2 => new string('a', 201), _ => "Valid" };
        var description = scenario == 3 ? new string('a', 2001) : "Updated";
        Assert.ThrowsAny<ArgumentException>(() => workflow.UpdateDetails(name, description, CreatedAt.AddMinutes(4)));
        Assert.Equal(before, Capture(workflow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_mutation_rejects_time_before_updated_at_without_effects(int operation)
    {
        var workflow = NewWorkflow();
        PublishTrigger(workflow);
        if (operation is 1 or 2)
            workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(4));
        var before = Capture(workflow);
        var earlier = workflow.UpdatedAt.AddTicks(-1).ToOffset(TimeSpan.FromHours(5));
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            switch (operation)
            {
                case 0: workflow.CreateDraft(Id(1001), earlier); break;
                case 1: workflow.ReplaceDraftGraph([Trigger(versionId: Id(1001))], [], earlier); break;
                case 2: workflow.PublishDraft(earlier); break;
                case 3: workflow.UpdateDetails("Changed", "Changed", earlier); break;
                case 4: workflow.Archive(earlier); break;
            }
        });
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Same_instant_with_different_offset_is_allowed_and_normalized()
    {
        var workflow = NewWorkflow();
        var instant = workflow.UpdatedAt.ToOffset(TimeSpan.FromHours(9));
        workflow.CreateDraft(VersionId, instant);
        workflow.ReplaceDraftGraph([Trigger()], [], instant);
        workflow.PublishDraft(instant);
        workflow.UpdateDetails("Orders", null, instant);
        workflow.Archive(instant);
        Assert.Equal(TimeSpan.Zero, workflow.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, workflow.ArchivedAt!.Value.Offset);
        Assert.Equal(instant.ToUniversalTime(), workflow.ArchivedAt);
        Assert.Equal(5, workflow.Revision);
    }

    [Fact]
    public void Graph_edit_and_publish_require_active_draft()
    {
        var workflow = NewWorkflow();
        var before = Capture(workflow);
        Assert.Throws<InvalidOperationException>(() => workflow.ReplaceDraftGraph([], [], CreatedAt));
        Assert.Throws<InvalidOperationException>(() => workflow.PublishDraft(CreatedAt));
        Assert.Equal(before, Capture(workflow));
    }

    [Fact]
    public void Archive_preserves_publication_and_draft_but_terminates_all_mutations()
    {
        var workflow = NewWorkflow();
        var published = PublishTrigger(workflow);
        var draft = workflow.CreateDraft(Id(1001), CreatedAt.AddMinutes(4));
        workflow.Archive(CreatedAt.AddMinutes(5));
        Assert.Equal(5, workflow.Revision);
        Assert.Equal(CreatedAt.AddMinutes(5).ToUniversalTime(), workflow.ArchivedAt);
        Assert.Same(draft, workflow.DraftVersion);
        Assert.Equal(published.Id, workflow.CurrentPublishedVersionId);
        Assert.Equal(2, published.Revision);
        Assert.Equal(0, draft.Revision);
        var before = Capture(workflow);
        Assert.Throws<InvalidOperationException>(() => workflow.CreateDraft(Id(1002), CreatedAt.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => workflow.ReplaceDraftGraph([], [], CreatedAt.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => workflow.PublishDraft(CreatedAt.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => workflow.UpdateDetails("Changed", null, CreatedAt.AddMinutes(6)));
        Assert.Throws<InvalidOperationException>(() => workflow.Archive(CreatedAt.AddMinutes(6)));
        Assert.Equal(before, Capture(workflow));
    }

    private static IEnumerable<T> YieldThenThrow<T>(T item)
    {
        yield return item;
        throw new InvalidOperationException("Input enumeration failed.");
    }

    private static void AssertReadonly<T>(IReadOnlyList<T> values)
    {
        if (values is IList<T> generic)
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        if (values is IList untyped)
            Assert.Throws<NotSupportedException>(() => untyped.Clear());
    }

    private static string Capture(Workflow workflow) => JsonSerializer.Serialize(new
    {
        workflow.Id, workflow.OwnerUserId, workflow.Name, workflow.Description,
        workflow.CreatedAt, workflow.UpdatedAt, workflow.ArchivedAt, workflow.Revision,
        workflow.CurrentPublishedVersionId, DraftId = workflow.DraftVersion?.Id,
        Versions = workflow.Versions.Select(v => new
        {
            v.Id, v.WorkflowId, v.OwnerUserId, v.VersionNumber, v.Status, v.CreatedAt,
            v.PublishedAt, v.Revision,
            Nodes = v.Nodes.Select(n => new { n.NodeId, n.WorkflowVersionId, n.Type, n.Position, n.Credential }),
            Connections = v.Connections.Select(c => new { c.Id, c.WorkflowVersionId, c.SourceNodeId, c.TargetNodeId, c.SourcePort })
        })
    });
}
