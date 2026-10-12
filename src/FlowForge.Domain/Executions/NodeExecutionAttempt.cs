
namespace FlowForge.Domain.Executions;

public enum NodeAttemptStatus { Running = 1, Succeeded = 2, Failed = 3, Cancelled = 4, Interrupted = 5 }
public enum ExternalOutcome { NotApplicable = 1, NotStarted = 2, KnownSuccess = 3, KnownFailure = 4, Unknown = 5 }

public sealed record NodeAttemptSnapshot(Guid Id, Guid ExecutionId, Guid NodeExecutionId, int Number,
    NodeAttemptStatus Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, ExecutionFailureCode? ErrorCode,
    ExternalOutcome ExternalOutcome, DateTimeOffset? DispatchedAt, int? HttpStatusCode,
    int? CredentialRevisionUsed, DateTimeOffset? RetryAt, bool Legacy);

public sealed class NodeExecutionAttempt
{
    public NodeAttemptSnapshot Snapshot { get; private set; }
    public NodeExecutionAttempt(Guid id, Guid executionId, Guid nodeExecutionId, int number, DateTimeOffset startedAt, bool external)
    {
        if (new[] { id, executionId, nodeExecutionId }.Contains(Guid.Empty) || number < 1) throw new ArgumentException("Tentativa exige identidades e número válidos.");
        // Conservador desde o início: queda antes de registrar dispatch não autoriza repetição de POST.
        Snapshot = new(id, executionId, nodeExecutionId, number, NodeAttemptStatus.Running, startedAt.ToUniversalTime(), null,
            null, external ? ExternalOutcome.Unknown : ExternalOutcome.NotApplicable, null, null, null, null, false);
    }
    public static NodeExecutionAttempt Restore(NodeAttemptSnapshot snapshot)
    {
        var result = new NodeExecutionAttempt(snapshot.Id, snapshot.ExecutionId, snapshot.NodeExecutionId, snapshot.Number, snapshot.StartedAt, false);
        var terminal = snapshot.Status != NodeAttemptStatus.Running;
        if (!Enum.IsDefined(snapshot.Status) || !Enum.IsDefined(snapshot.ExternalOutcome) ||
            (terminal ? snapshot.FinishedAt is null || snapshot.FinishedAt < snapshot.StartedAt : snapshot.FinishedAt is not null || snapshot.ErrorCode is not null) ||
            (snapshot.Status is NodeAttemptStatus.Failed or NodeAttemptStatus.Interrupted ? snapshot.ErrorCode is null || !Enum.IsDefined(snapshot.ErrorCode.Value) : snapshot.ErrorCode is not null) ||
            (snapshot.HttpStatusCode.HasValue && snapshot.HttpStatusCode is < 100 or > 599) ||
            (snapshot.CredentialRevisionUsed.HasValue && snapshot.CredentialRevisionUsed < 1) ||
            (snapshot.DispatchedAt.HasValue && (snapshot.DispatchedAt < snapshot.StartedAt || snapshot.DispatchedAt > snapshot.FinishedAt)) ||
            (snapshot.RetryAt.HasValue && (snapshot.FinishedAt is null || snapshot.RetryAt <= snapshot.FinishedAt)))
            throw new ArgumentException("Histórico de tentativa inconsistente.");
        result.Snapshot = snapshot; return result;
    }
    public void RecordDispatch(int? revision, DateTimeOffset now)
    {
        if (Snapshot.Status != NodeAttemptStatus.Running || Snapshot.DispatchedAt.HasValue || now < Snapshot.StartedAt || revision is < 1)
            throw new InvalidOperationException("Tentativa não pode iniciar outro envio.");
        Snapshot = Snapshot with { DispatchedAt = now.ToUniversalTime(), CredentialRevisionUsed = revision, ExternalOutcome = ExternalOutcome.Unknown };
    }
    public void Finish(NodeAttemptStatus status, DateTimeOffset now, ExecutionFailureCode? error, ExternalOutcome outcome,
        int? httpStatus = null, int? credentialRevision = null, DateTimeOffset? retryAt = null)
    {
        if (Snapshot.Status != NodeAttemptStatus.Running || status == NodeAttemptStatus.Running) throw new InvalidOperationException("Tentativa já concluída.");
        var next = Snapshot with { Status = status, FinishedAt = now.ToUniversalTime(), ErrorCode = error, ExternalOutcome = outcome,
            HttpStatusCode = httpStatus, CredentialRevisionUsed = credentialRevision ?? Snapshot.CredentialRevisionUsed, RetryAt = retryAt };
        Snapshot = Restore(next).Snapshot;
    }
}
