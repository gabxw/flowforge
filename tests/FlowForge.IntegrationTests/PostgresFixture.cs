using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowForge.IntegrationTests;

[CollectionDefinition("PostgreSQL")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowforge_tests")
        .WithUsername("flowforge_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public string ConnectionString => container.GetConnectionString();
    public IDbContextFactory<FlowForgeDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        Factory = new ContextFactory(new DbContextOptionsBuilder<FlowForgeDbContext>()
            .UseNpgsql(ConnectionString).Options);
        await using var context = await Factory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private sealed class ContextFactory(DbContextOptions<FlowForgeDbContext> options)
        : IDbContextFactory<FlowForgeDbContext>
    {
        public FlowForgeDbContext CreateDbContext() => new(options);
    }
}
