using FlowForge.Domain.Executions;

namespace FlowForge.Application.Executions;

public sealed class ExecutionService(IExecutionStore store)
{
    public Task<WorkflowExecutionSnapshot> RequestAsync(Guid owner, Guid workflowId, CancellationToken ct = default)
    {
        Validate(owner); Validate(workflowId);
        return store.RequestAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), workflowId, owner), ct);
    }

    public async Task<WorkflowExecutionSnapshot> GetAsync(Guid owner, Guid id, CancellationToken ct = default)
    {
        Validate(owner); Validate(id);
        return await store.GetAsync(id, owner, ct) ?? throw new KeyNotFoundException("Execução não encontrada.");
    }

    private static void Validate(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A identidade não pode ser vazia.");
    }
}

public sealed class ExecutionMessageHandler(IExecutionInboxStore inbox) : IExecutionMessageHandler
{
    public async Task<MessageDisposition> HandleAsync(ExecutionRequestedMessage message, CancellationToken ct = default)
    {
        var claim = await inbox.TryClaimAsync(message, TimeSpan.FromSeconds(30), ct);
        return claim.Status switch
        {
            InboxClaimStatus.Invalid => MessageDisposition.Invalid,
            InboxClaimStatus.Completed => MessageDisposition.Completed,
            InboxClaimStatus.Busy => MessageDisposition.Busy,
            _ => await inbox.CompleteEngineUnavailableAsync(claim, ct) ? MessageDisposition.Completed : MessageDisposition.Busy
        };
    }
}

public sealed class OutboxDispatcher(IExecutionOutboxStore outbox, IExecutionPublisher publisher)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct = default)
    {
        var claim = await outbox.TryClaimAsync(TimeSpan.FromSeconds(30), ct);
        if (claim is null) return false;
        try
        {
            await publisher.PublishAsync(claim.Message, ct);
            return await outbox.MarkPublishedAsync(claim, ct);
        }
        catch
        {
            // Na interrupção, a lease expira. Nenhuma transação espera pela rede.
            if (!ct.IsCancellationRequested) await outbox.ReleaseAsync(claim, TimeSpan.FromSeconds(2), ct);
            throw;
        }
    }
}
