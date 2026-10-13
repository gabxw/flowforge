using System.Diagnostics;
using FlowForge.Domain.Executions;
using FlowForge.Domain.Workflows;
using FlowForge.Domain.Workflows.Configuration;

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
                var state = checkpoint.Nodes.Single(n => n.NodeId == node.NodeId);
                var canReplay = executor?.CanReplayAfterInterruption == true && state.AttemptCount < ExecutionLimits.MaximumLocalAttempts;
                if (node.Configuration is HttpRequestConfiguration retry)
                    canReplay = retry.CanRepeat && state.AttemptCount < retry.MaxAttempts;
                var start = await store.BeginNodeAsync(claim, checkpoint.Revision, node.NodeId, canReplay, ct);
                if (start.Status == NodeStartStatus.Completed) return MessageDisposition.Completed;
                if (start.Status == NodeStartStatus.Lost) return MessageDisposition.Busy;
                using var nodeSpan = ExecutionTelemetry.Node(checkpoint.Execution.Id, checkpoint.Execution.CorrelationId, node.NodeId, node.Type, start.AttemptNumber);
                var activeStarted = Stopwatch.GetTimestamp();
                NodeResult result;
                if (checkpoint.Execution.ResumeAt.HasValue && node.Type == NodeType.Delay)
                    result = node.Type == NodeType.Delay && checkpoint.Nodes.Single(n => n.NodeId == node.NodeId).Status == NodeExecutionStatus.Running
                        ? NodeResult.Success(checkpoint.Input) : NodeResult.Failure(ExecutionFailureCode.InvalidExecutorResult);
                else if (!hasExecutor || executor is null)
                    result = NodeResult.Failure(ExecutionFailureCode.UnsupportedNode);
                else if (checkpoint.Nodes.Single(n => n.NodeId == node.NodeId).Status == NodeExecutionStatus.Running &&
                    !canReplay)
                    result = NodeResult.Failure(executor.CanReplayAfterInterruption ? ExecutionFailureCode.AttemptLimitExceeded : ExecutionFailureCode.InterruptedNode);
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(processing.Token);
                    timeout.CancelAfter(checkpoint.DeadlineRemaining is { } remaining && remaining < options.NodeTimeout
                        ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.FromTicks(1)) : options.NodeTimeout);
                    try
                    {
                        result = await executor.ExecuteAsync(new(checkpoint.Execution.Id, checkpoint.Execution.CorrelationId,
                            node, checkpoint.Input.Clone(), checkpoint.Execution.OwnerUserId, start.AttemptNumber,
                            async (revision, token) => {
                                var permission = await store.BeforeExternalCallAsync(claim, start.Revision, node.NodeId, revision, token);
                                if (permission != LeaseStatus.Active) lease.Status = permission;
                                return permission;
                            }), timeout.Token).WaitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (lease.Status == LeaseStatus.Lost) { return MessageDisposition.Busy; }
                    catch (OperationCanceledException) when (lease.Status == LeaseStatus.CancelRequested) { result = NodeResult.Cancelled(); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { result = NodeResult.Failure(ExecutionFailureCode.NodeTimeout); }
                    catch (ExecutionAuthorityLostException) { return MessageDisposition.Busy; }
                    catch (OperationCanceledException) when (lease.Status == LeaseStatus.DeadlineExceeded) { result = NodeResult.Failure(ExecutionFailureCode.ExecutionDeadlineExceeded); }
                    catch (Exception) { result = NodeResult.Failure(ExecutionFailureCode.NodeFailed); }
                }
                Guid? next = null;
                try
                {
                    if (result is null || (result.HttpStatusCode.HasValue && (node.Type != NodeType.HttpRequest || result.HttpStatusCode is < 100 or > 599)) ||
                        (result.RetryAfter.HasValue && (node.Type != NodeType.HttpRequest || result.RetryAfter < TimeSpan.Zero)) ||
                        (result.ExternalOutcome.HasValue && (node.Type != NodeType.HttpRequest || !Enum.IsDefined(result.ExternalOutcome.Value))) || (result.SuspendFor is { } duration &&
                        (node.Configuration is not DelayConfiguration delay || duration != delay.Duration ||
                         result.IsCancelled || result.ErrorCode.HasValue || result.LogMessage is not null || result.CredentialRevisionUsed.HasValue)) ||
                        (result.CredentialRevisionUsed is { } credentialRevision &&
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
                var outcome = saved == CheckpointWriteStatus.Lost ? "authority_lost" : result.IsCancelled ? "cancelled" : result.ErrorCode.HasValue ? "failed" : saved == CheckpointWriteStatus.Suspended ? "suspended" : "succeeded";
                ExecutionTelemetry.RecordNode(node.Type, outcome, Stopwatch.GetElapsedTime(activeStarted));
                nodeSpan?.SetTag("flowforge.outcome", outcome);
                if (result.ErrorCode.HasValue) { nodeSpan?.SetTag("error.type", result.ErrorCode.Value.ToString()); nodeSpan?.SetStatus(ActivityStatusCode.Error, "node_failed"); }
                if (checkpoint.Execution.ResumeAt.HasValue && node.Type == NodeType.Delay && state.StartedAt.HasValue)
                    ExecutionTelemetry.WaitDuration.Record(Math.Max(0, (DateTimeOffset.UtcNow - state.StartedAt.Value).TotalSeconds), new KeyValuePair<string, object?>("kind", "delay"));
                if (saved is CheckpointWriteStatus.Completed or CheckpointWriteStatus.Suspended) return MessageDisposition.Completed;
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
