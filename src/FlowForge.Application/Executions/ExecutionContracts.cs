using FlowForge.Domain.Executions;

namespace FlowForge.Application.Executions;

public sealed record ExecutionRequest(Guid ExecutionId, Guid MessageId, Guid CorrelationId,
    Guid WorkflowId, Guid OwnerUserId);

// IDs e contexto somente: nenhum payload, configuração de node ou secret vai para a fila.
public sealed record ExecutionRequestedMessage(int ContractVersion, Guid MessageId, Guid ExecutionId, Guid CorrelationId)
{
    public const int CurrentVersion = 1;
    public const string MessageType = "workflow.execution.requested.v1";
    public bool IsValid() => ContractVersion == CurrentVersion && MessageId != Guid.Empty &&
        ExecutionId != Guid.Empty && CorrelationId != Guid.Empty;
}

public interface IExecutionStore
{
    Task<WorkflowExecutionSnapshot> RequestAsync(ExecutionRequest request, CancellationToken ct = default);
    Task<WorkflowExecutionSnapshot?> GetAsync(Guid executionId, Guid owner, CancellationToken ct = default);
    Task<WorkflowExecutionSnapshot?> RequestCancellationAsync(Guid executionId, Guid owner, CancellationToken ct = default);
    Task<ExecutionHistory?> HistoryAsync(Guid executionId, Guid owner, CancellationToken ct = default);
}

public sealed record OutboxClaim(ExecutionRequestedMessage Message, Guid Token);
public interface IExecutionOutboxStore
{
    Task<OutboxClaim?> TryClaimAsync(TimeSpan lease, CancellationToken ct = default);
    Task<bool> MarkPublishedAsync(OutboxClaim claim, CancellationToken ct = default);
    Task ReleaseAsync(OutboxClaim claim, TimeSpan delay, CancellationToken ct = default);
}

public enum InboxClaimStatus { Acquired, Busy, Completed, Invalid }
public sealed record InboxClaim(InboxClaimStatus Status, Guid ExecutionId, Guid MessageId, Guid? Token, int Generation = 0);
public interface IExecutionInboxStore
{
    Task<InboxClaim> TryClaimAsync(ExecutionRequestedMessage message, TimeSpan lease, CancellationToken ct = default);
}

public interface IExecutionPublisher
{
    Task PublishAsync(ExecutionRequestedMessage message, CancellationToken ct = default);
}

public enum MessageDisposition { Completed, Busy, Invalid }
public interface IExecutionMessageHandler
{
    Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default);
}
