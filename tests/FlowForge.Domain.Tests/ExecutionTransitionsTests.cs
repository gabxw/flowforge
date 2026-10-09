using FlowForge.Domain.Executions;
using Xunit;

namespace FlowForge.Domain.Tests;

public sealed class ExecutionTransitionsTests
{
    public static IEnumerable<object[]> WorkflowTransitions()
    {
        int[] states = [1, 2, 3, 4, 5, 0, -1, 6, int.MinValue, int.MaxValue];
        bool[,] expected =
        {
            { false, true,  false, false, true,  false, false, false, false, false },
            { false, false, true,  true,  true,  false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false }
        };

        for (var from = 0; from < states.Length; from++)
        for (var to = 0; to < states.Length; to++)
            yield return [states[from], states[to], expected[from, to]];
    }

    public static IEnumerable<object[]> NodeTransitions()
    {
        int[] states = [1, 2, 3, 4, 5, 6, 7, 0, -1, 8, int.MinValue, int.MaxValue];
        bool[,] expected =
        {
            { false, true,  false, false, false, true,  false, false, false, false, false, false },
            { false, false, true,  true,  true,  false, true,  false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, true,  false, false, false, false, true,  false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false },
            { false, false, false, false, false, false, false, false, false, false, false, false }
        };

        for (var from = 0; from < states.Length; from++)
        for (var to = 0; to < states.Length; to++)
            yield return [states[from], states[to], expected[from, to]];
    }

    [Theory]
    [MemberData(nameof(WorkflowTransitions))]
    public void Workflow_can_transition_matches_the_operational_policy(int from, int to, bool expected)
    {
        Assert.Equal(expected, ExecutionTransitions.CanTransition(
            (WorkflowExecutionStatus)from, (WorkflowExecutionStatus)to));
    }

    [Theory]
    [MemberData(nameof(WorkflowTransitions))]
    public void Workflow_ensure_transition_enforces_the_operational_policy(int from, int to, bool expected)
    {
        var exception = Record.Exception(() => ExecutionTransitions.EnsureTransition(
            (WorkflowExecutionStatus)from, (WorkflowExecutionStatus)to));

        if (expected)
            Assert.Null(exception);
        else
            Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [MemberData(nameof(NodeTransitions))]
    public void Node_can_transition_matches_the_operational_policy(int from, int to, bool expected)
    {
        Assert.Equal(expected, ExecutionTransitions.CanTransition(
            (NodeExecutionStatus)from, (NodeExecutionStatus)to));
    }

    [Theory]
    [MemberData(nameof(NodeTransitions))]
    public void Node_ensure_transition_enforces_the_operational_policy(int from, int to, bool expected)
    {
        var exception = Record.Exception(() => ExecutionTransitions.EnsureTransition(
            (NodeExecutionStatus)from, (NodeExecutionStatus)to));

        if (expected)
            Assert.Null(exception);
        else
            Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData(WorkflowExecutionStatus.Pending, 1)]
    [InlineData(WorkflowExecutionStatus.Running, 2)]
    [InlineData(WorkflowExecutionStatus.Succeeded, 3)]
    [InlineData(WorkflowExecutionStatus.Failed, 4)]
    [InlineData(WorkflowExecutionStatus.Cancelled, 5)]
    public void Workflow_status_preserves_its_numeric_contract(WorkflowExecutionStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }

    [Theory]
    [InlineData(NodeExecutionStatus.Pending, 1)]
    [InlineData(NodeExecutionStatus.Running, 2)]
    [InlineData(NodeExecutionStatus.Succeeded, 3)]
    [InlineData(NodeExecutionStatus.Failed, 4)]
    [InlineData(NodeExecutionStatus.Retrying, 5)]
    [InlineData(NodeExecutionStatus.Skipped, 6)]
    [InlineData(NodeExecutionStatus.Cancelled, 7)]
    public void Node_status_preserves_its_numeric_contract(NodeExecutionStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }
}
