using FlowForge.Infrastructure.Messaging;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

namespace FlowForge.IntegrationTests;

[CollectionDefinition("Dispatch")]
public sealed class DispatchCollection : ICollectionFixture<DispatchFixture>;

public sealed class DispatchFixture : IAsyncLifetime
{
    private readonly string rabbitPassword = Guid.NewGuid().ToString("N");
    public PostgreSqlFixture Postgres { get; } = new();
    public RabbitMqContainer Rabbit { get; private set; } = null!;
    public IDbContextFactory<FlowForgeDbContext> Factory => Postgres.Factory;
    public RabbitRuntimeOptions RabbitOptions => new(Rabbit.Hostname, Rabbit.GetMappedPublicPort(5672), "flowforge_tests", rabbitPassword);

    public async Task InitializeAsync()
    {
        Rabbit = new RabbitMqBuilder("rabbitmq:4.2-management-alpine").WithUsername("flowforge_tests")
            .WithPassword(rabbitPassword).Build();
        await Task.WhenAll(Postgres.InitializeAsync(), Rabbit.StartAsync());
    }

    public async Task<IConnection> ConnectAsync() =>
        await new ConnectionFactory
        {
            HostName = Rabbit.Hostname, Port = Rabbit.GetMappedPublicPort(5672),
            UserName = "flowforge_tests", Password = rabbitPassword, AutomaticRecoveryEnabled = false
        }.CreateConnectionAsync();

    public async Task ResetAsync()
    {
        // Apenas banco/fila descartáveis desta collection; nunca usa ambiente Compose operacional.
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE inbox_messages, outbox_messages, workflow_executions");
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await ExecutionRabbitTopology.DeclareAsync(channel);
        await channel.QueuePurgeAsync(ExecutionRabbitTopology.Queue);
    }

    public async Task DisposeAsync()
    {
        if (Rabbit is not null) await Rabbit.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}
