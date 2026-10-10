using FlowForge.Application.Executions;
using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
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
    private readonly string keyDirectory = Path.Combine(Path.GetTempPath(), "flowforge-tests-" + Guid.NewGuid().ToString("N"));
    public ExecutionContextProtection Protection => new(DataProtectionProvider.Create(Directory.CreateDirectory(keyDirectory), b => b.SetApplicationName("FlowForge")));
    public PostgresExecutionEngineStore EngineStore => new(Factory, Protection);
    public SequentialExecutionEngine Engine(IEnumerable<INodeExecutor>? executors = null, EngineOptions? options = null) =>
        new(EngineStore, executors ?? [new TriggerNodeExecutor(), new LogNodeExecutor()], options ?? EngineOptions.Default);
    public ExecutionMessageHandler Handler() => new(new PostgresExecutionInboxStore(Factory), Engine());
    public async Task<bool> CompleteAsync(InboxClaim claim) => await Engine().RunAsync(claim) == MessageDisposition.Completed;
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
        await db.Database.ExecuteSqlRawAsync("TRUNCATE webhook_idempotency, execution_logs, node_executions, inbox_messages, outbox_messages, workflow_executions");
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await ExecutionRabbitTopology.DeclareAsync(channel);
        await channel.QueuePurgeAsync(ExecutionRabbitTopology.Queue);
    }

    public async Task DisposeAsync()
    {
        if (Rabbit is not null) await Rabbit.DisposeAsync();
        await Postgres.DisposeAsync();
        if (Directory.Exists(keyDirectory)) Directory.Delete(keyDirectory, true);
    }
}
