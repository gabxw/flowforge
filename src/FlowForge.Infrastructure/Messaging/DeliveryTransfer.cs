
using System.Text.Json;
using FlowForge.Application.Executions;
using RabbitMQ.Client;

namespace FlowForge.Infrastructure.Messaging;
public enum DeliveryFailureCode { InvalidContract = 1, UnknownIdentity = 2, ProcessingExhausted = 3 }
public sealed record DeadLetterEnvelope(int Version, Guid ReceiptId, DeliveryFailureCode Reason, int Failures, ExecutionRequestedMessage? Message);
internal static class DeliveryTransfer
{
    internal const string RetryHeader = "x-flowforge-delivery-attempt";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static int? Counter(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue(RetryHeader, out var value) != true) return 0;
        var number = value switch { byte v => v, short v => v, int v => v, long v => v, _ => -1L };
        return number is >= 0 and <= 5 ? (int)number : null;
    }
    internal static async Task RetryAsync(IChannel channel, ExecutionRequestedMessage message, int failures, CancellationToken ct)
    {
        using var retrySpan = ExecutionTelemetry.Start("rabbitmq.retry", System.Diagnostics.ActivityKind.Producer);
        var headers = RabbitTraceContext.Headers(); headers[RetryHeader] = failures;
        // Reconstrói o contrato conhecido; não copia headers arbitrários nem body original.
        await channel.BasicPublishAsync(ExecutionRabbitTopology.Exchange, ExecutionRabbitTopology.RoutingKey, mandatory: true,
            basicProperties: new BasicProperties { Persistent = true, ContentType = "application/json", Type = ExecutionRequestedMessage.MessageType,
                MessageId = message.MessageId.ToString(), CorrelationId = message.CorrelationId.ToString(),
                Headers = headers },
            body: ExecutionMessageJson.Serialize(message), cancellationToken: ct);
    }
    internal static async Task DeadLetterAsync(IChannel channel, ExecutionRequestedMessage? message, DeliveryFailureCode reason, int failures, CancellationToken ct)
    {
        using var deadLetterSpan = ExecutionTelemetry.Start("rabbitmq.dead_letter", System.Diagnostics.ActivityKind.Producer);
        var receipt = Guid.NewGuid();
        // A DLQ não recebe payload/headers/exceções, inclusive quando um produtor envia conteúdo malicioso.
        var envelope = new DeadLetterEnvelope(1, receipt, reason, failures, message);
        await channel.BasicPublishAsync(ExecutionRabbitTopology.DeadLetterExchange, ExecutionRabbitTopology.DeadLetterRoutingKey, mandatory: true,
            basicProperties: new BasicProperties { Persistent = true, ContentType = "application/json", Type = "workflow.execution.dead-letter.v1",
                MessageId = receipt.ToString(), CorrelationId = message?.CorrelationId.ToString() ?? receipt.ToString(), Headers = RabbitTraceContext.Headers() },
            body: JsonSerializer.SerializeToUtf8Bytes(envelope, Json), cancellationToken: ct);
        ExecutionTelemetry.DeadLetters.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
    }
}
