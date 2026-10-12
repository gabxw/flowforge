
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Persistence.Records;

namespace FlowForge.Infrastructure.Persistence;
internal static class AttemptPersistence
{
    internal static NodeAttemptSnapshot Snapshot(NodeAttemptRecord r) => NodeExecutionAttempt.Restore(new(r.Id, r.ExecutionId,
        r.NodeExecutionId, r.Number, r.Status, r.StartedAt, r.FinishedAt, r.ErrorCode, r.ExternalOutcome,
        r.DispatchedAt, r.HttpStatusCode, r.CredentialRevisionUsed, r.RetryAt, r.Legacy)).Snapshot;
    internal static void Apply(NodeAttemptRecord r, NodeAttemptSnapshot s)
    {
        r.Id = s.Id; r.ExecutionId = s.ExecutionId; r.NodeExecutionId = s.NodeExecutionId; r.Number = s.Number;
        r.Status = s.Status; r.StartedAt = s.StartedAt; r.FinishedAt = s.FinishedAt; r.ErrorCode = s.ErrorCode;
        r.ExternalOutcome = s.ExternalOutcome; r.DispatchedAt = s.DispatchedAt; r.HttpStatusCode = s.HttpStatusCode;
        r.CredentialRevisionUsed = s.CredentialRevisionUsed; r.RetryAt = s.RetryAt; r.Legacy = s.Legacy;
    }
}
