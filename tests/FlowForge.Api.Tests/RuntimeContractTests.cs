using System.Net;
using System.Text.Json;
using FlowForge.Application.Workflows;
using FlowForge.Domain.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public async Task Errors_remain_sanitized_problem_details_when_Accept_prefers_plain_text_in_Development()
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowForge:TechnicalOwnerId"] = Guid.NewGuid().ToString(),
                ["FLOWFORGE_POSTGRES_CONNECTION_STRING"] = "Host=127.0.0.1;Database=unused"
            }));
            builder.ConfigureServices(services => services.AddScoped<IWorkflowStore, FailingStore>());
        });
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
        using var error = await client.GetAsync("/api/workflows");
        var problem = await Problem(error, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("secret-sentinel", problem.ToJsonString());
        Assert.DoesNotContain(nameof(InvalidOperationException), problem.ToJsonString());
        using var missing = await client.GetAsync("/route-that-does-not-exist");
        await Problem(missing, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("invalid-owner", "Host=127.0.0.1;Database=unused")]
    [InlineData("4190b033-4b2b-49be-9517-4eb8847d1023", "invalid-key=secret-sentinel")]
    [InlineData("4190b033-4b2b-49be-9517-4eb8847d1023", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=secret-sentinel;Timeout=1")]
    public async Task Missing_or_unavailable_runtime_returns_sanitized_503_and_liveness_still_works(string? owner, string? connection)
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FlowForge:TechnicalOwnerId"] = owner,
                ["FLOWFORGE_POSTGRES_CONNECTION_STRING"] = connection,
                ["FlowForge:Postgres:PasswordFile"] = null
            }));
        });
        using var client = api.CreateClient();
        using var response = await client.GetAsync("/api/workflows");
        var problem = await Problem(response, HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain("secret-sentinel", problem.ToJsonString());
        Assert.DoesNotContain("Npgsql", problem.ToJsonString());
        using var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenAPI_describes_workflow_operations_errors_and_polymorphic_configurations_without_database()
    {
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = api.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/workflows", out var collection));
        Assert.True(collection.TryGetProperty("post", out _));
        var publish = paths.GetProperty("/api/workflows/{id}/publish").GetProperty("post");
        Assert.True(publish.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(publish.GetProperty("responses").TryGetProperty("409", out _));
        var draftSchema = paths.GetProperty("/api/workflows/{id}/draft").GetProperty("put")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.EndsWith("/ReplaceDraftRequest", draftSchema.GetProperty("$ref").GetString());
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var configurations = schemas.EnumerateObject().Single(p => p.Name == "ConfigurationDto").Value;
        Assert.Equal("type", configurations.GetProperty("discriminator").GetProperty("propertyName").GetString());
        Assert.Equal(6, configurations.GetProperty("discriminator").GetProperty("mapping").EnumerateObject().Count());
    }

    private sealed class FailingStore : IWorkflowStore
    {
        public Task AddAsync(Workflow workflow, CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret-sentinel");
        public Task<Workflow?> GetAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret-sentinel");
        public Task SaveAsync(Workflow workflow, int expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret-sentinel");
        public Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid ownerUserId, int offset = 0, int limit = 20, bool includeArchived = false,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("secret-sentinel");
    }
}
