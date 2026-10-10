using FlowForge.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    private readonly IDataProtectionProvider testProvider = new EphemeralDataProtectionProvider();
    private ExecutionContextProtection TestProtection => new(testProvider);
    public WebApplicationFactory<Program> CreateApi(Guid? owner = null, int permits = 60, bool protectWebhookInput = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["FLOWFORGE_POSTGRES_CONNECTION_STRING"] = container.GetConnectionString(),
                    ["FlowForge:TechnicalOwnerId"] = (owner ?? Guid.NewGuid()).ToString(),
                    ["FlowForge:Webhooks:PermitLimit"] = permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["FlowForge:Webhooks:WindowSeconds"] = "3600"
                }));
            // Somente testes: substitui explicitamente a proteção lazy do runtime Production.
            if (protectWebhookInput) builder.ConfigureTestServices(services => { services.AddSingleton(testProvider); services.AddSingleton(TestProtection); });
        });

    public async Task DisposeAsync() => await container.DisposeAsync();
}
