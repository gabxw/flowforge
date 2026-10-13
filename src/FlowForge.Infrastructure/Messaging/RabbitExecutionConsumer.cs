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
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
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
            var counter = DeliveryTransfer.Counter(delivery.BasicProperties);
            if (counter is null || message is null || delivery.BasicProperties.Type != ExecutionRequestedMessage.MessageType ||
                delivery.BasicProperties.ContentType != "application/json" ||
                delivery.BasicProperties.MessageId != message.MessageId.ToString() ||
                delivery.BasicProperties.CorrelationId != message.CorrelationId.ToString())
            {
                logger.LogWarning("Mensagem rejeitada: contrato inválido. EventCode={EventCode}", "invalid_message");
                await DeliveryTransfer.DeadLetterAsync(channel, message, DeliveryFailureCode.InvalidContract, counter ?? 0, session.Token);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: session.Token);
                return;
            }
            using var processSpan = ExecutionTelemetry.Start("workflow.process", System.Diagnostics.ActivityKind.Consumer, RabbitTraceContext.Read(delivery.BasicProperties));
            processSpan?.SetTag("messaging.system", "rabbitmq"); processSpan?.SetTag("messaging.destination.name", ExecutionRabbitTopology.Queue);
            processSpan?.SetTag("flowforge.execution.id", message.ExecutionId.ToString()); processSpan?.SetTag("flowforge.correlation.id", message.CorrelationId.ToString());
            using var correlationScope = logger.BeginScope(new Dictionary<string, object> { ["ExecutionId"] = message.ExecutionId, ["CorrelationId"] = message.CorrelationId });
            ExecutionTelemetry.Deliveries.Add(1);
            MessageDisposition result;
            try { result = await handler.HandleAsync(message, session.Token); }
            catch (OperationCanceledException) when (session.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                logger.LogWarning("Recebimento pendente. ExecutionId={ExecutionId} ErrorType={ErrorType}", message.ExecutionId, e.GetType().Name);
                var failures = counter.Value + 1;
                if (failures >= 5)
                    await DeliveryTransfer.DeadLetterAsync(channel, message, DeliveryFailureCode.ProcessingExhausted, failures, session.Token);
                else
                {
                    var seconds = Math.Min(8, Math.Pow(2, failures - 1)) * (0.5 + Random.Shared.NextDouble() * 0.5);
                    await Task.Delay(TimeSpan.FromSeconds(seconds), session.Token);
                    await DeliveryTransfer.RetryAsync(channel, message, failures, session.Token);
                }
                // Falha antes/depois do confirm pode duplicar, mas nunca confirma a entrega antes da cópia durável.
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: session.Token);
                return;
            }
            if (result == MessageDisposition.Invalid)
            {
                logger.LogWarning("Mensagem rejeitada: identidade desconhecida. MessageId={MessageId}", message.MessageId);
                await DeliveryTransfer.DeadLetterAsync(channel, message, DeliveryFailureCode.UnknownIdentity, counter.Value, session.Token);
            }
            // Busy é duplicata. Lease vigente/scanner independente mantêm o trabalho durável.
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: session.Token);
            if (result != MessageDisposition.Invalid)
                logger.LogInformation("Despacho concluído. ExecutionId={ExecutionId} CorrelationId={CorrelationId} EventCode={EventCode}",
                    message.ExecutionId, message.CorrelationId, result == MessageDisposition.Busy ? "busy_ack" : "completed");
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
