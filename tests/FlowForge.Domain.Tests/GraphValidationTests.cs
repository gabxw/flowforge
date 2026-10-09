using System.Collections;
using FlowForge.Domain.Workflows;
using Xunit;
using static FlowForge.Domain.Tests.GraphFixtures;

namespace FlowForge.Domain.Tests;

public sealed class GraphValidationTests
{
    [Fact]
    public void Trigger_alone_is_publishable() => Assert.Empty(Validate([Trigger()], []));

    [Fact]
    public void Linear_graph_ignores_visual_order()
    {
        Assert.Empty(Validate([Log(3), Trigger(), Log()], [Edge(10, 1, 2), Edge(11, 2, 3)]));
    }

    [Fact]
    public void Condition_can_send_both_ports_to_the_same_node_without_false_cycle()
    {
        Assert.Empty(Validate([Trigger(), Condition(), Log(3)],
            [Edge(10, 1, 2), Edge(11, 2, 3, "true"), Edge(12, 2, 3, "false")]));
    }

    [Fact]
    public void Distinct_condition_branches_can_converge()
    {
        Assert.Empty(Validate([Trigger(), Condition(), Log(3), Log(4), Log(5)],
            [Edge(10, 1, 2), Edge(11, 2, 3, "true"), Edge(12, 2, 4, "false"),
             Edge(13, 3, 5), Edge(14, 4, 5)]));
    }

    [Fact]
    public void Empty_graph_reports_empty_graph_and_missing_trigger()
    {
        var errors = Validate([], []);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.EmptyGraph);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.TriggerCount);
    }

    [Fact]
    public void No_trigger_and_multiple_triggers_are_rejected()
    {
        Assert.Contains(Validate([Log()], []), e => e.Code == GraphErrorCode.TriggerCount);
        Assert.Contains(Validate([Trigger(), Trigger(2)], []), e => e.Code == GraphErrorCode.TriggerCount);
    }

    [Fact]
    public void Duplicate_node_ids_report_error_instead_of_throwing()
    {
        Assert.Contains(Validate([Trigger(), Log(), Log()], [Edge(10, 1, 2)]),
            e => e.Code == GraphErrorCode.DuplicateNodeId && e.NodeId == Id(2));
    }

    [Fact]
    public void Duplicate_connection_ids_report_error_instead_of_throwing()
    {
        Assert.Contains(Validate([Trigger(), Log(), Log(3)], [Edge(10, 1, 2), Edge(10, 2, 3)]),
            e => e.Code == GraphErrorCode.DuplicateConnectionId && e.ConnectionId == Id(10));
    }

    [Fact]
    public void Version_and_owner_errors_accumulate_with_node_and_connection_identity()
    {
        var errors = Validate([Trigger(), Http(2, Id(9999), Id(9998))],
            [Edge(10, 1, 2, versionId: Id(9997))]);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.NodeVersionMismatch && e.NodeId == Id(2));
        Assert.Contains(errors, e => e.Code == GraphErrorCode.CredentialOwnerMismatch && e.NodeId == Id(2));
        Assert.Contains(errors, e => e.Code == GraphErrorCode.ConnectionVersionMismatch && e.ConnectionId == Id(10));
    }

    [Fact]
    public void Credential_reference_owned_by_workflow_owner_is_valid()
    {
        Assert.Empty(Validate([Trigger(), Http(2, OwnerId)], [Edge(10, 1, 2)]));
    }

    [Fact]
    public void Both_missing_endpoints_are_reported_without_key_lookup_failure()
    {
        var errors = Validate([Trigger()], [Edge(10, 8, 9)]);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.MissingSourceNode && e.ConnectionId == Id(10));
        Assert.Contains(errors, e => e.Code == GraphErrorCode.MissingTargetNode && e.ConnectionId == Id(10));
    }

    [Fact]
    public void Incoming_trigger_connection_is_rejected()
    {
        Assert.Contains(Validate([Trigger(), Log()], [Edge(10, 1, 2), Edge(11, 2, 1)]),
            e => e.Code == GraphErrorCode.TriggerHasIncomingConnection && e.ConnectionId == Id(11));
    }

    [Theory]
    [InlineData("NEXT")]
    [InlineData("true")]
    [InlineData(" next ")]
    [InlineData("other")]
    public void Ordinary_node_only_accepts_ordinal_next_port(string port)
    {
        Assert.Contains(Validate([Trigger(), Log()], [Edge(10, 1, 2, port)]),
            e => e.Code == GraphErrorCode.InvalidSourcePort && e.ConnectionId == Id(10));
    }

    [Theory]
    [InlineData("next")]
    [InlineData("True")]
    [InlineData("FALSE")]
    public void Condition_rejects_unknown_and_case_changed_ports(string port)
    {
        Assert.Contains(Validate([Trigger(), Condition(), Log(3)],
            [Edge(10, 1, 2), Edge(11, 2, 3, port)]), e => e.Code == GraphErrorCode.InvalidSourcePort);
    }

    [Fact]
    public void Ordinary_node_cannot_fan_out_on_repeated_next_port()
    {
        Assert.Contains(Validate([Trigger(), Log(), Log(3)], [Edge(10, 1, 2), Edge(11, 1, 3)]),
            e => e.Code == GraphErrorCode.DuplicateSourcePort && e.NodeId == Id(1));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Condition_requires_each_branch_even_when_it_has_two_edges(string repeatedPort)
    {
        var errors = Validate([Trigger(), Condition(), Log(3), Log(4)],
            [Edge(10, 1, 2), Edge(11, 2, 3, repeatedPort), Edge(12, 2, 4, repeatedPort)]);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.DuplicateSourcePort && e.NodeId == Id(2));
        Assert.Contains(errors, e => e.Code == GraphErrorCode.ConditionBranchesIncomplete && e.NodeId == Id(2));
    }

    [Fact]
    public void Condition_without_connections_is_incomplete()
    {
        Assert.Contains(Validate([Trigger(), Condition()], [Edge(10, 1, 2)]),
            e => e.Code == GraphErrorCode.ConditionBranchesIncomplete && e.NodeId == Id(2));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Condition_with_only_one_branch_is_incomplete(string port)
    {
        Assert.Contains(Validate([Trigger(), Condition(), Log(3)],
            [Edge(10, 1, 2), Edge(11, 2, 3, port)]),
            e => e.Code == GraphErrorCode.ConditionBranchesIncomplete && e.NodeId == Id(2));
    }

    [Fact]
    public void Reachable_cycle_is_rejected()
    {
        Assert.Contains(Validate([Trigger(), Log(), Log(3)],
            [Edge(10, 1, 2), Edge(11, 2, 3), Edge(12, 3, 2)]), e => e.Code == GraphErrorCode.Cycle);
    }

    [Fact]
    public void Disconnected_cycle_reports_cycle_and_each_unreachable_node()
    {
        var errors = Validate([Trigger(), Log(), Log(3)], [Edge(10, 2, 3), Edge(11, 3, 2)]);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.Cycle);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.UnreachableNode && e.NodeId == Id(2));
        Assert.Contains(errors, e => e.Code == GraphErrorCode.UnreachableNode && e.NodeId == Id(3));
    }

    [Fact]
    public void Self_loop_is_rejected()
    {
        Assert.Contains(Validate([Trigger(), Log()], [Edge(10, 1, 2), Edge(11, 2, 2)]),
            e => e.Code == GraphErrorCode.Cycle);
    }

    [Fact]
    public void Unconnected_node_is_rejected()
    {
        Assert.Contains(Validate([Trigger(), Log()], []),
            e => e.Code == GraphErrorCode.UnreachableNode && e.NodeId == Id(2));
    }

    [Fact]
    public void Fifty_nodes_are_accepted()
    {
        var nodes = new List<WorkflowNode> { Trigger() };
        nodes.AddRange(Enumerable.Range(2, 49).Select(i => Log(i)));
        var edges = Enumerable.Range(1, 49).Select(i => Edge(100 + i, i, i + 1)).ToArray();
        Assert.Empty(Validate(nodes, edges));
    }

    [Fact]
    public void One_hundred_connections_reach_graph_validation_instead_of_limit_rejection()
    {
        var edges = Enumerable.Range(1, 100).Select(i => Edge(i, 1, 2)).ToArray();
        var errors = Validate([Trigger(), Log()], edges);
        Assert.DoesNotContain(errors, e => e.Code == GraphErrorCode.ConnectionLimitExceeded);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.DuplicateSourcePort);
    }

    [Fact]
    public void Fifty_one_nodes_return_limit_error_without_reading_elements()
    {
        var errors = Validate(new CountOnlyList<WorkflowNode>(51), new CountOnlyList<WorkflowConnection>(0));
        Assert.Equal(GraphErrorCode.NodeLimitExceeded, Assert.Single(errors).Code);
    }

    [Fact]
    public void One_hundred_one_connections_return_limit_error_without_reading_elements()
    {
        var errors = Validate(new CountOnlyList<WorkflowNode>(1), new CountOnlyList<WorkflowConnection>(101));
        Assert.Equal(GraphErrorCode.ConnectionLimitExceeded, Assert.Single(errors).Code);
    }

    [Fact]
    public void Both_exceeded_limits_are_reported_without_reading_graph()
    {
        var errors = Validate(new CountOnlyList<WorkflowNode>(51), new CountOnlyList<WorkflowConnection>(101));
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.NodeLimitExceeded);
        Assert.Contains(errors, e => e.Code == GraphErrorCode.ConnectionLimitExceeded);
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => WorkflowGraphValidator.Validate(Guid.Empty, OwnerId, [], []));
        Assert.Throws<ArgumentException>(() => WorkflowGraphValidator.Validate(VersionId, Guid.Empty, [], []));
        Assert.Throws<ArgumentNullException>(() => WorkflowGraphValidator.Validate(VersionId, OwnerId, null!, []));
        Assert.Throws<ArgumentNullException>(() => WorkflowGraphValidator.Validate(VersionId, OwnerId, [], null!));
        Assert.Throws<ArgumentException>(() => Validate([null!], []));
        Assert.Throws<ArgumentException>(() => Validate([Trigger()], [null!]));
    }

    [Fact]
    public void Error_collection_cannot_be_modified_by_cast()
    {
        var errors = Validate([], []);
        if (errors is IList<GraphValidationError> list)
            Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.NotEmpty(errors);
    }

    private static IReadOnlyList<GraphValidationError> Validate(
        IReadOnlyList<WorkflowNode> nodes, IReadOnlyList<WorkflowConnection> edges) =>
        WorkflowGraphValidator.Validate(VersionId, OwnerId, nodes, edges);

    private sealed class CountOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Must not read oversized graph.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Must not enumerate oversized graph.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
