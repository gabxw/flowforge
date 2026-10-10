namespace FlowForge.Domain.Executions;

public enum ExecutionFailureCode
{
    EngineUnavailable = 1, UnsupportedNode = 2, NodeFailed = 3, NodeTimeout = 4,
    InterruptedNode = 5, InvalidExecutorResult = 6, ContextLimitExceeded = 7,
    HttpDestinationDenied = 8, CredentialUnavailable = 9, HttpRemoteFailure = 10,
    HttpResponseLimitExceeded = 11, HttpResponseInvalid = 12, HttpTransportFailed = 13, HttpResponseSensitive = 14,
    ConditionValueNotComparable = 15, TransformSourceMissing = 16, JsonPointerAmbiguous = 17, TransformValueInvalid = 18
}

public sealed record WorkflowExecutionSnapshot(Guid Id, Guid WorkflowId, Guid WorkflowVersionId,
    Guid OwnerUserId, Guid CorrelationId, WorkflowExecutionStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, ExecutionFailureCode? ErrorCode, DateTimeOffset? CancelRequestedAt = null, DateTimeOffset? ResumeAt = null);

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
            WorkflowExecutionStatus.Succeeded => snapshot.StartedAt >= snapshot.CreatedAt && snapshot.FinishedAt >= snapshot.StartedAt && snapshot.ErrorCode is null,
            WorkflowExecutionStatus.Cancelled => snapshot.FinishedAt >= (snapshot.StartedAt ?? snapshot.CreatedAt) &&
                (snapshot.StartedAt is null || snapshot.StartedAt >= snapshot.CreatedAt) && snapshot.CancelRequestedAt.HasValue &&
                snapshot.FinishedAt >= snapshot.CancelRequestedAt && snapshot.ErrorCode is null,
            _ => false
        };
        if (snapshot.ResumeAt.HasValue && (snapshot.Status != WorkflowExecutionStatus.Running || snapshot.ResumeAt <= snapshot.StartedAt)) valid = false;
        if (!valid || (snapshot.CancelRequestedAt.HasValue && snapshot.CancelRequestedAt < snapshot.CreatedAt)) throw new ArgumentException("Estado persistido da execução inconsistente.");
        execution.Snapshot = snapshot;
        return execution;
    }

    public void Suspend(DateTimeOffset resumeAt)
    {
        if (Snapshot.Status != WorkflowExecutionStatus.Running || Snapshot.ResumeAt.HasValue)
            throw new InvalidOperationException("Suspensão exige execução ativa sem espera anterior.");
        if (resumeAt <= Snapshot.StartedAt) throw new ArgumentOutOfRangeException(nameof(resumeAt));
        Snapshot = Snapshot with { ResumeAt = resumeAt.ToUniversalTime() };
    }

    public void Resume(DateTimeOffset now)
    {
        if (Snapshot.Status != WorkflowExecutionStatus.Running || Snapshot.ResumeAt is null)
            throw new InvalidOperationException("Retomada exige uma espera persistida.");
        if (now < Snapshot.ResumeAt) throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot = Snapshot with { ResumeAt = null };
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (now < Snapshot.CreatedAt) throw new ArgumentOutOfRangeException(nameof(now));
        if (Snapshot.Status is WorkflowExecutionStatus.Pending or WorkflowExecutionStatus.Running && Snapshot.CancelRequestedAt is null)
            Snapshot = Snapshot with { CancelRequestedAt = now.ToUniversalTime() };
    }

    public void Succeed(DateTimeOffset now)
    {
        Finish(WorkflowExecutionStatus.Succeeded, now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Snapshot.CancelRequestedAt is null) throw new InvalidOperationException("Cancelamento exige um pedido persistido.");
        if (now < Snapshot.CancelRequestedAt) throw new ArgumentOutOfRangeException(nameof(now));
        Finish(WorkflowExecutionStatus.Cancelled, now);
    }

    private void Finish(WorkflowExecutionStatus status, DateTimeOffset now)
    {
        ExecutionTransitions.EnsureTransition(Snapshot.Status, status);
        if (now < (Snapshot.StartedAt ?? Snapshot.CreatedAt)) throw new ArgumentOutOfRangeException(nameof(now));
        Snapshot = Snapshot with { Status = status, FinishedAt = now.ToUniversalTime(), ResumeAt = null };
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
        Snapshot = Snapshot with { Status = WorkflowExecutionStatus.Failed, FinishedAt = now.ToUniversalTime(), ErrorCode = code, ResumeAt = null };
    }
}
