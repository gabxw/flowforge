using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Messaging;

namespace FlowForge.Worker;

public sealed class OutboxDispatchService(OutboxDispatcher dispatcher, ILogger<OutboxDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await dispatcher.DispatchOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogWarning("Publicação pendente. EventCode={EventCode} ErrorType={ErrorType}", "outbox_pending", e.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

public sealed class ExecutionConsumerService(RabbitExecutionConsumer consumer, ILogger<ExecutionConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await consumer.RunSessionAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogWarning("Consumer desconectado. EventCode={EventCode} ErrorType={ErrorType}", "consumer_disconnected", e.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
