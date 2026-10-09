using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
using static FlowForge.Api.Tests.WorkflowEndpointTests;

namespace FlowForge.Api.Tests;

public sealed class RuntimeContractTests
{
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
}
