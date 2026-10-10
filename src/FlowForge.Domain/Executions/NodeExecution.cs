namespace FlowForge.Domain.Executions;

// Captura conservadora: conteúdo arbitrário nunca é um snapshot público.
public sealed record PayloadSummary(int ByteLength, string Kind);
public sealed record NodeExecutionSnapshot(Guid Id, Guid ExecutionId, Guid WorkflowVersionId, Guid NodeId,
    NodeExecutionStatus Status, int AttemptCount, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    PayloadSummary? Input, PayloadSummary? Output, ExecutionFailureCode? ErrorCode);

public sealed class NodeExecution
{
    public NodeExecutionSnapshot Snapshot { get; private set; }
    public NodeExecution(Guid id, Guid executionId, Guid versionId, Guid nodeId)
    {
        if (new[] { id, executionId, versionId, nodeId }.Contains(Guid.Empty))
            throw new ArgumentException("Identidades do node não podem ser vazias.");
        Snapshot = new(id, executionId, versionId, nodeId, NodeExecutionStatus.Pending, 0, null, null, null, null, null);
    }

    public static NodeExecution Restore(NodeExecutionSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var node = new NodeExecution(s.Id, s.ExecutionId, s.WorkflowVersionId, s.NodeId);
        var active = s.AttemptCount > 0 && s.StartedAt.HasValue && s.Input is not null;
        var finished = active && s.FinishedAt >= s.StartedAt;
        var valid = s.Status switch
        {
            NodeExecutionStatus.Pending => s.AttemptCount == 0 && s.StartedAt is null && s.FinishedAt is null &&
                s.Input is null && s.Output is null && s.ErrorCode is null,
            NodeExecutionStatus.Running => active && s.FinishedAt is null && s.Output is null && s.ErrorCode is null,
            NodeExecutionStatus.Succeeded => finished && s.Output is not null && s.ErrorCode is null,
            NodeExecutionStatus.Failed => finished && s.Output is null && s.ErrorCode.HasValue && Enum.IsDefined(s.ErrorCode.Value),
            NodeExecutionStatus.Cancelled => finished && s.Output is null && s.ErrorCode is null,
            NodeExecutionStatus.Skipped => s.AttemptCount == 0 && s.StartedAt is null && s.FinishedAt.HasValue &&
                s.Input is null && s.Output is null && s.ErrorCode is null,
            _ => false // Retry e histórico de tentativas entram na Fase 10.
        };
        Validate(s.Input); Validate(s.Output);
        if (!valid) throw new ArgumentException("Estado persistido do node inconsistente.");
        node.Snapshot = s;
        return node;
    }

    public void Start(PayloadSummary input, DateTimeOffset now)
    {
        ExecutionTransitions.EnsureTransition(Snapshot.Status, NodeExecutionStatus.Running);
        ArgumentNullException.ThrowIfNull(input); Validate(input);
        Snapshot = Snapshot with { Status = NodeExecutionStatus.Running, AttemptCount = 1,
            StartedAt = now.ToUniversalTime(), Input = input };
    }

    // Replay local seguro após interrupção; a engine decide se o executor permite isso.
    public void Resume()
    {
        if (Snapshot.Status != NodeExecutionStatus.Running) throw new InvalidOperationException("O node não está interrompido em Running.");
        Snapshot = Snapshot with { AttemptCount = checked(Snapshot.AttemptCount + 1) };
    }

    public void Succeed(PayloadSummary output, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(output); Validate(output);
        Finish(NodeExecutionStatus.Succeeded, now);
        Snapshot = Snapshot with { Output = output };
    }

    public void Fail(ExecutionFailureCode code, DateTimeOffset now)
    {
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        Finish(NodeExecutionStatus.Failed, now); Snapshot = Snapshot with { ErrorCode = code };
    }

    public void Cancel(DateTimeOffset now) => Finish(NodeExecutionStatus.Cancelled, now);
    public void Skip(DateTimeOffset now) => Finish(NodeExecutionStatus.Skipped, now);

    private void Finish(NodeExecutionStatus status, DateTimeOffset now)
    {
        ExecutionTransitions.EnsureTransition(Snapshot.Status, status);
        if (Snapshot.StartedAt.HasValue && now < Snapshot.StartedAt) throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot = Snapshot with { Status = status, FinishedAt = now.ToUniversalTime() };
    }

    private static void Validate(PayloadSummary? summary)
    {
        if (summary is not null && (summary.ByteLength < 0 || summary.ByteLength > 65536 ||
            summary.Kind is not ("Object" or "Array" or "String" or "Number" or "True" or "False" or "Null")))
            throw new ArgumentException("Metadados de payload inválidos.");
    }
}
