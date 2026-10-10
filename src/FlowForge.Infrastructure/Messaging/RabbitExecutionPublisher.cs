using FlowForge.Application.Executions;
using RabbitMQ.Client;

namespace FlowForge.Infrastructure.Messaging;

public sealed class RabbitExecutionPublisher(RabbitRuntimeOptions options) : IExecutionPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private IChannel? channel;

    public async Task PublishAsync(ExecutionRequestedMessage message, CancellationToken ct = default)
    {
        var body = ExecutionMessageJson.Serialize(message);
        await gate.WaitAsync(ct); // Um canal AMQP não pode receber publicações concorrentes.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (channel?.IsOpen != true || connection?.IsOpen != true)
            {
                await ResetAsync();
                connection = await options.CreateFactory("flowforge:outbox").CreateConnectionAsync(timeout.Token);
                channel = await connection.CreateChannelAsync(new CreateChannelOptions(
                    publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), timeout.Token);
                await ExecutionRabbitTopology.DeclareAsync(channel, timeout.Token);
            }
            await channel.BasicPublishAsync(ExecutionRabbitTopology.Exchange, ExecutionRabbitTopology.RoutingKey,
                mandatory: true, basicProperties: new BasicProperties
                {
                    Persistent = true, ContentType = "application/json", Type = ExecutionRequestedMessage.MessageType,
                    MessageId = message.MessageId.ToString(), CorrelationId = message.CorrelationId.ToString()
                }, body: body, cancellationToken: timeout.Token);
        }
        catch
        {
            await ResetAsync();
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task ResetAsync()
    {
        if (channel is not null)
        {
            try { await channel.DisposeAsync(); } catch { /* A lease/outbox mantém a recuperação possível. */ }
            channel = null;
        }
        if (connection is not null)
        {
            try { await connection.DisposeAsync(); } catch { }
            connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await ResetAsync(); } finally { gate.Release(); gate.Dispose(); }
    }
}
