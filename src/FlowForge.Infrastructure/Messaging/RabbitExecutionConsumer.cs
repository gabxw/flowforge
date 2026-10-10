using FlowForge.Application.Executions;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FlowForge.Infrastructure.Messaging;

public sealed class RabbitExecutionConsumer(RabbitRuntimeOptions options, IExecutionMessageHandler handler,
    ILogger<RabbitExecutionConsumer> logger)
{
    // O host refaz a sessão se falhar. Isso também cobre a primeira conexão, sem dois mecanismos de recovery.
    public async Task RunSessionAsync(CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var connection = await options.CreateFactory("flowforge:consumer").CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
        channel.CallbackExceptionAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
        await ExecutionRabbitTopology.DeclareAsync(channel, ct);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken: ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) => { disconnected.TrySetResult(); return Task.CompletedTask; };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            // Deserializa antes de sair do callback; o buffer pertence ao cliente RabbitMQ.
            var message = ExecutionMessageJson.Deserialize(delivery.Body);
            if (message is null || delivery.BasicProperties.Type != ExecutionRequestedMessage.MessageType ||
                delivery.BasicProperties.ContentType != "application/json" ||
                delivery.BasicProperties.MessageId != message.MessageId.ToString() ||
                delivery.BasicProperties.CorrelationId != message.CorrelationId.ToString())
            {
                logger.LogWarning("Mensagem rejeitada: contrato inválido. EventCode={EventCode}", "invalid_message");
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: session.Token);
                return;
            }
            while (!session.IsCancellationRequested && channel.IsOpen)
            {
                try
                {
                    var result = await handler.HandleAsync(message, session.Token);
                    if (result == MessageDisposition.Completed)
                    {
                        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: session.Token);
                        logger.LogInformation("Despacho concluído. ExecutionId={ExecutionId} CorrelationId={CorrelationId}",
                            message.ExecutionId, message.CorrelationId);
                        return;
                    }
                    if (result == MessageDisposition.Invalid)
                    {
                        logger.LogWarning("Mensagem rejeitada: identidade desconhecida. MessageId={MessageId}", message.MessageId);
                        await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: session.Token);
                        return;
                    }
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    // Não registrar objeto/mensagem da exceção: providers podem incluir credenciais.
                    logger.LogWarning("Recebimento pendente. ExecutionId={ExecutionId} ErrorType={ErrorType}", message.ExecutionId, e.GetType().Name);
                }
                // Retém a entrega sem ack; evita loop de nack/requeue enquanto banco/claim estão indisponíveis.
                await Task.Delay(TimeSpan.FromSeconds(1), session.Token);
            }
        };
        await channel.BasicConsumeAsync(ExecutionRabbitTopology.Queue, autoAck: false, consumer, cancellationToken: ct);
        logger.LogInformation("Consumer conectado. Queue={Queue}", ExecutionRabbitTopology.Queue);
        try { await disconnected.Task.WaitAsync(ct); }
        finally
        {
            session.Cancel();
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await channel.CloseAsync(cancellationToken: close.Token); } catch { }
        }
    }
}
