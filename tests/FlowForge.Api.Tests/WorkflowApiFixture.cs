using FlowForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowForge.Api.Tests;

[CollectionDefinition("Workflow API")]
public sealed class WorkflowApiCollection : ICollectionFixture<WorkflowApiFixture>;

public sealed class WorkflowApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowforge_api_tests")
        .WithUsername("flowforge_api_tests")
        .WithPassword(Guid.NewGuid().ToString("N"))
        .Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var db = new FlowForgeDbContext(new DbContextOptionsBuilder<FlowForgeDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options);
        await db.Database.MigrateAsync();
    }

    public WebApplicationFactory<Program> CreateApi(Guid? owner = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FLOWFORGE_POSTGRES_CONNECTION_STRING"] = container.GetConnectionString(),
                    ["FlowForge:TechnicalOwnerId"] = (owner ?? Guid.NewGuid()).ToString()
                }));
        });

    public async Task DisposeAsync() => await container.DisposeAsync();
}
