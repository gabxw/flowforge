using RabbitMQ.Client;

namespace FlowForge.Infrastructure.Messaging;

public static class ExecutionRabbitTopology
{
    public const string Exchange = "flowforge.executions";
    public const string Queue = "flowforge.executions.v1";
    public const string RoutingKey = "execution.requested.v1";
    public const string DeadLetterExchange = "flowforge.executions.dead";
    public const string DeadLetterQueue = "flowforge.executions.dead.v1";
    public const string DeadLetterRoutingKey = "execution.rejected.v1";

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct = default)
    {
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, DeadLetterRoutingKey, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: ct);
    }
}
