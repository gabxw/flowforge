namespace FlowForge.Domain.Executions;

public enum ExecutionFailureCode { EngineUnavailable = 1 }

public sealed record WorkflowExecutionSnapshot(Guid Id, Guid WorkflowId, Guid WorkflowVersionId,
    Guid OwnerUserId, Guid CorrelationId, WorkflowExecutionStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, ExecutionFailureCode? ErrorCode);

public sealed class WorkflowExecution
{
    public WorkflowExecutionSnapshot Snapshot { get; private set; }

    public WorkflowExecution(Guid id, Guid workflowId, Guid versionId, Guid owner, Guid correlationId,
        DateTimeOffset createdAt)
    {
        if (new[] { id, workflowId, versionId, owner, correlationId }.Contains(Guid.Empty))
            throw new ArgumentException("Identidades de execução não podem ser vazias.");
        Snapshot = new(id, workflowId, versionId, owner, correlationId, WorkflowExecutionStatus.Pending,
            createdAt.ToUniversalTime(), null, null, null);
    }

    public static WorkflowExecution Restore(WorkflowExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var execution = new WorkflowExecution(snapshot.Id, snapshot.WorkflowId, snapshot.WorkflowVersionId,
            snapshot.OwnerUserId, snapshot.CorrelationId, snapshot.CreatedAt);
        var valid = snapshot.Status switch
        {
            WorkflowExecutionStatus.Pending => snapshot.StartedAt is null && snapshot.FinishedAt is null && snapshot.ErrorCode is null,
            WorkflowExecutionStatus.Running => snapshot.StartedAt >= snapshot.CreatedAt && snapshot.FinishedAt is null && snapshot.ErrorCode is null,
            WorkflowExecutionStatus.Failed => snapshot.StartedAt >= snapshot.CreatedAt && snapshot.FinishedAt >= snapshot.StartedAt &&
                snapshot.ErrorCode.HasValue && Enum.IsDefined(snapshot.ErrorCode.Value),
            _ => false // Os demais resultados serão implementados com a engine.
        };
        if (!valid) throw new ArgumentException("Estado persistido da execução inconsistente.");
        execution.Snapshot = snapshot;
        return execution;
    }

    public void Start(DateTimeOffset now)
    {
        ExecutionTransitions.EnsureTransition(Snapshot.Status, WorkflowExecutionStatus.Running);
        if (now < Snapshot.CreatedAt) throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot = Snapshot with { Status = WorkflowExecutionStatus.Running, StartedAt = now.ToUniversalTime() };
    }

    public void Fail(ExecutionFailureCode code, DateTimeOffset now)
    {
        ExecutionTransitions.EnsureTransition(Snapshot.Status, WorkflowExecutionStatus.Failed);
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        if (now < Snapshot.StartedAt) throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot = Snapshot with { Status = WorkflowExecutionStatus.Failed, FinishedAt = now.ToUniversalTime(), ErrorCode = code };
    }
}
