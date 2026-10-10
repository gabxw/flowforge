using System.Text.Json;
using FlowForge.Application.Executions;
using FlowForge.Domain.Executions;
using FlowForge.Infrastructure.Persistence.Records;

namespace FlowForge.Infrastructure.Persistence;
internal static class NodeExecutionPersistence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static NodeExecutionSnapshot Snapshot(NodeExecutionRecord r) => NodeExecution.Restore(new(r.Id, r.ExecutionId,
        r.WorkflowVersionId, r.NodeId, r.Status, r.AttemptCount, r.StartedAt, r.FinishedAt,
        r.InputSummary is null ? null : JsonSerializer.Deserialize<PayloadSummary>(r.InputSummary, Json),
        r.OutputSummary is null ? null : JsonSerializer.Deserialize<PayloadSummary>(r.OutputSummary, Json), r.ErrorCode, r.CredentialRevisionUsed)).Snapshot;
    internal static void Apply(NodeExecutionRecord r, NodeExecutionSnapshot s)
    {
        r.CredentialRevisionUsed = s.CredentialRevisionUsed;
        r.Status = s.Status; r.AttemptCount = s.AttemptCount; r.StartedAt = s.StartedAt; r.FinishedAt = s.FinishedAt;
        r.InputSummary = s.Input is null ? null : JsonSerializer.Serialize(s.Input, Json);
        r.OutputSummary = s.Output is null ? null : JsonSerializer.Serialize(s.Output, Json); r.ErrorCode = s.ErrorCode;
    }
    internal static ExecutionLogSummary Log(ExecutionLogRecord r) => new(r.Id, r.ExecutionId, r.NodeExecutionId,
        r.CreatedAt, r.EventCode, r.MessageByteLength);
}
