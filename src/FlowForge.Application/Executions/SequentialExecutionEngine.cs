using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;

namespace FlowForge.Application.Executions;

public sealed class SequentialExecutionEngine
{
    private readonly IExecutionEngineStore store;
    private readonly IReadOnlyDictionary<NodeType, INodeExecutor> executors;
    private readonly EngineOptions options;
    public SequentialExecutionEngine(IExecutionEngineStore store, IEnumerable<INodeExecutor> executors, EngineOptions options)
    {
        options.Validate();
        this.store = store; this.executors = executors.ToDictionary(e => e.Type); this.options = options;
    }

    public TimeSpan Lease => options.Lease;

    public async Task<MessageDisposition> RunAsync(InboxClaim claim, CancellationToken ct = default)
    {
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stopHeartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lease = new LeaseObservation();
        var heartbeat = HeartbeatAsync(claim, lease, processing, stopHeartbeat.Token);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (lease.Status == LeaseStatus.Lost) return MessageDisposition.Busy;
                var checkpoint = await store.LoadAsync(claim, ct);
                if (checkpoint is null) return MessageDisposition.Busy;
                var path = new ExecutionPath(checkpoint.Version, checkpoint.Execution.OwnerUserId);
                var node = path.Node(checkpoint.NextNodeId);
                var hasExecutor = executors.TryGetValue(node.Type, out var executor);
                var start = await store.BeginNodeAsync(claim, checkpoint.Revision, node.NodeId, executor?.CanReplayAfterInterruption ?? false, ct);
                if (start.Status == NodeStartStatus.Completed) return MessageDisposition.Completed;
                if (start.Status == NodeStartStatus.Lost) return MessageDisposition.Busy;
                NodeResult result;
                if (!hasExecutor || executor is null)
                    result = NodeResult.Failure(ExecutionFailureCode.UnsupportedNode);
                else if (checkpoint.Nodes.Single(n => n.NodeId == node.NodeId).Status == NodeExecutionStatus.Running &&
                    !executor.CanReplayAfterInterruption)
                    result = NodeResult.Failure(ExecutionFailureCode.InterruptedNode);
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(processing.Token);
                    timeout.CancelAfter(options.NodeTimeout);
                    try
                    {
                        result = await executor.ExecuteAsync(new(checkpoint.Execution.Id, checkpoint.Execution.CorrelationId,
                            node, checkpoint.Input.Clone(), checkpoint.Execution.OwnerUserId), timeout.Token).WaitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (lease.Status == LeaseStatus.Lost) { return MessageDisposition.Busy; }
                    catch (OperationCanceledException) when (lease.Status == LeaseStatus.CancelRequested) { result = NodeResult.Cancelled(); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { result = NodeResult.Failure(ExecutionFailureCode.NodeTimeout); }
                    catch (Exception) { result = NodeResult.Failure(ExecutionFailureCode.NodeFailed); }
                }
                Guid? next = null;
                try
                {
                    if (result is null || (result.CredentialRevisionUsed is { } credentialRevision &&
                        (credentialRevision < 1 || node.Type != NodeType.HttpRequest || node.Credential is null)) || (result.ErrorCode.HasValue && !Enum.IsDefined(result.ErrorCode.Value)) ||
                        (result.IsCancelled && (result.Output.HasValue || result.ErrorCode.HasValue)) ||
                        (result.ErrorCode.HasValue && result.Output.HasValue) ||
                        (result.LogMessage is not null && (node.Type != NodeType.Log || string.IsNullOrWhiteSpace(result.LogMessage) || result.LogMessage.Length > 2000)) ||
                        (result.IsCancelled && lease.Status != LeaseStatus.CancelRequested && checkpoint.Execution.CancelRequestedAt is null) ||
                        (!result.IsCancelled && result.ErrorCode is null && !result.Output.HasValue))
                        throw new ArgumentException("Resultado inválido do executor.");
                    if (result.Output.HasValue)
                    {
                        _ = ExecutionPayload.Summarize(result.Output.Value);
                        next = path.Next(node.NodeId, result.Port);
                    }
                }
                catch (ExecutionPayloadLimitException) { result = NodeResult.Failure(ExecutionFailureCode.ContextLimitExceeded); }
                catch (ArgumentException) { result = NodeResult.Failure(ExecutionFailureCode.InvalidExecutorResult); }
                var saved = await store.SaveNodeAsync(claim, start.Revision, node.NodeId, result, next, ct);
                if (saved == CheckpointWriteStatus.Completed) return MessageDisposition.Completed;
                if (saved == CheckpointWriteStatus.Lost) return MessageDisposition.Busy;
            }
        }
        finally
        {
            await stopHeartbeat.CancelAsync();
            await heartbeat;
        }
    }

    private async Task HeartbeatAsync(InboxClaim claim, LeaseObservation observation,
        CancellationTokenSource processing, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var status = await store.RenewAsync(claim, options.Lease, ct);
                observation.Status = status;
                if (status != LeaseStatus.Active) { await processing.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            // Falha de renovação perde autoridade local; mensagem permanece retomável.
            observation.Status = LeaseStatus.Lost;
            await processing.CancelAsync();
        }
    }

    private sealed class LeaseObservation
    {
        private int status;
        public LeaseStatus Status { get => (LeaseStatus)Volatile.Read(ref status); set => Volatile.Write(ref status, (int)value); }
    }
}
