using System.Text.Json;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;

namespace FlowForge.Application.Executions;

public sealed record ExecutionCheckpoint(WorkflowExecutionSnapshot Execution, WorkflowVersionSnapshot Version,
    int Revision, Guid NextNodeId, JsonElement Input, IReadOnlyList<NodeExecutionSnapshot> Nodes, TimeSpan? DeadlineRemaining = null);
public enum LeaseStatus { Active, CancelRequested, Lost, DeadlineExceeded }
public enum NodeStartStatus { Started, Completed, Lost }
public sealed record NodeStart(NodeStartStatus Status, int Revision, int AttemptNumber = 1);
public enum CheckpointWriteStatus { Saved, Completed, Lost, Suspended }
public interface IExecutionEngineStore
{
    Task<ExecutionCheckpoint?> LoadAsync(InboxClaim claim, CancellationToken ct = default);
    Task<LeaseStatus> BeforeExternalCallAsync(InboxClaim claim, int revision, Guid nodeId, int? credentialRevision, CancellationToken ct = default);
    Task<LeaseStatus> RenewAsync(InboxClaim claim, TimeSpan lease, CancellationToken ct = default);
    Task<NodeStart> BeginNodeAsync(InboxClaim claim, int revision, Guid nodeId, bool allowReplay = false, CancellationToken ct = default);
    Task<CheckpointWriteStatus> SaveNodeAsync(InboxClaim claim, int revision, Guid nodeId,
        NodeResult result, Guid? nextNodeId, CancellationToken ct = default);
}

public sealed record NodeRunContext(Guid ExecutionId, Guid CorrelationId, WorkflowNode Node, JsonElement Input, Guid OwnerUserId = default, int AttemptNumber = 1,
    Func<int?, CancellationToken, Task<LeaseStatus>>? BeforeExternalCall = null);
public sealed record NodeResult(JsonElement? Output, string Port, ExecutionFailureCode? ErrorCode,
    bool IsCancelled = false, string? LogMessage = null, int? CredentialRevisionUsed = null, TimeSpan? SuspendFor = null, int? HttpStatusCode = null, TimeSpan? RetryAfter = null, ExternalOutcome? ExternalOutcome = null)
{
    public static NodeResult Success(JsonElement output, string port = "next", string? logMessage = null, int? credentialRevisionUsed = null) =>
        new(output.Clone(), port, null, LogMessage: logMessage, CredentialRevisionUsed: credentialRevisionUsed);
    public static NodeResult Failure(ExecutionFailureCode code, int? credentialRevisionUsed = null) =>
        new(null, "next", code, CredentialRevisionUsed: credentialRevisionUsed);
    public static NodeResult Suspend(JsonElement input, TimeSpan duration) => new(input.Clone(), "next", null, SuspendFor: duration);
    public static NodeResult Cancelled() => new(null, "next", null, true);
}
public interface INodeExecutor
{
    NodeType Type { get; }
    bool CanReplayAfterInterruption { get; }
    Task<NodeResult> ExecuteAsync(NodeRunContext context, CancellationToken ct);
}

public sealed record EngineOptions(TimeSpan Lease, TimeSpan Heartbeat, TimeSpan NodeTimeout)
{
    public static EngineOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    public void Validate()
    {
        if (Lease <= TimeSpan.Zero || Lease > TimeSpan.FromMinutes(5) || Heartbeat <= TimeSpan.Zero ||
            Heartbeat >= Lease / 2 || NodeTimeout <= TimeSpan.Zero || NodeTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Limites operacionais da engine inválidos.");
    }
}

public static class ExecutionPayload
{
    public const int MaxBytes = 65536;
    public static PayloadSummary Summarize(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("JSON indefinido.");
        var size = JsonSerializer.SerializeToUtf8Bytes(value).Length;
        if (size > MaxBytes) throw new ExecutionPayloadLimitException();
        return new(size, value.ValueKind.ToString());
    }
}
public sealed class ExecutionPayloadLimitException : Exception;
public sealed class ExecutionAuthorityLostException : Exception;
public sealed class CredentialRevisionChangedException : Exception;
public static class ExecutionLimits
{
    public static TimeSpan MaximumDuration => TimeSpan.FromHours(48);
    public const int MaximumLocalAttempts = 5;
    public const int MaximumDeliveryClaims = 5;
}

public enum ExecutionLogCode { LogRecorded = 1 }
public sealed record ExecutionLogSummary(Guid Id, Guid ExecutionId, Guid NodeExecutionId, DateTimeOffset CreatedAt,
    ExecutionLogCode EventCode, int MessageByteLength);
public sealed record ExecutionHistory(IReadOnlyList<NodeExecutionSnapshot> Nodes, IReadOnlyList<ExecutionLogSummary> Logs, IReadOnlyList<NodeAttemptSnapshot>? Attempts = null);
