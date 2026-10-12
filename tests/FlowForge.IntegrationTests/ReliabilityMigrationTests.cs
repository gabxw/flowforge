
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowForge.IntegrationTests;
public sealed class ReliabilityMigrationTests
{
    [Fact]
    public async Task Upgrade_preserves_legacy_result_and_imports_only_the_known_summary()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("flowforge_upgrade")
            .WithUsername("flowforge_upgrade").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<FlowForgeDbContext>().UseNpgsql(container.GetConnectionString()).Options;
        await using var db = new FlowForgeDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261010194759_CondicoesTransformacoesEEsperaDuravel");
        var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<FlowForgeDbContext>(options);
        var owner = Guid.NewGuid(); var now = WorkflowStoreFixtures.Start;
        await new PostgresTechnicalUserStore(factory).EnsureExistsAsync(owner, now);
        var workflow = WorkflowStoreFixtures.Definition(owner);
        foreach (var reference in workflow.Versions.SelectMany(v => v.Nodes).Where(n => n.Credential is not null).Select(n => n.Credential!).DistinctBy(r => r.Id))
        {
            var metadata = new FlowForge.Domain.Credentials.Credential(reference.Id, owner, "Fictícia legada",
                FlowForge.Domain.Credentials.CredentialType.BearerToken, FlowForge.Domain.Credentials.HttpsOrigin.Parse("https://example.com"), null, now);
            await new PostgresCredentialStore(factory, new FlowForge.Infrastructure.Security.CredentialProtection(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()))
                .CreateAsync(metadata.Snapshot, new FlowForge.Application.Credentials.CredentialSecret(Guid.NewGuid().ToString("N")));
        }
        await new PostgresWorkflowStore(factory).AddAsync(workflow);
        var execution = Guid.NewGuid(); var node = Guid.NewGuid(); var correlation = Guid.NewGuid();
        var version = workflow.Versions.Single(v => v.Id == workflow.CurrentPublishedVersionId);
        var http = version.Nodes.Single(n => n.Type == FlowForge.Domain.Workflows.NodeType.HttpRequest);
        var started = DateTimeOffset.UtcNow.AddDays(-2); started = started.AddTicks(-(started.Ticks % 10));
        var finished = started.AddSeconds(2); var summary = """{"byteLength":2,"kind":"Object"}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO workflow_executions (id, workflow_id, workflow_version_id, owner_user_id, correlation_id, status, created_at, started_at, finished_at)
            VALUES ({execution}, {workflow.Id}, {version.Id}, {owner}, {correlation}, 3, {started}, {started}, {finished});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO node_executions (id, execution_id, workflow_version_id, node_id, ordinal, status, attempt_count, started_at, finished_at, input_summary, output_summary)
            VALUES ({node}, {execution}, {version.Id}, {http.NodeId}, 0, 3, 2, {started}, {finished}, CAST({summary} AS jsonb), CAST({summary} AS jsonb));
            """);
        await db.Database.MigrateAsync();
        var imported = await db.NodeAttempts.AsNoTracking().SingleAsync();
        Assert.True(imported.Legacy); Assert.Equal(2, imported.Number); Assert.Equal(started, imported.StartedAt); Assert.Equal(finished, imported.FinishedAt);
        Assert.Null(imported.DispatchedAt); Assert.Null(imported.HttpStatusCode);
        Assert.Equal(FlowForge.Domain.Executions.ExternalOutcome.KnownSuccess, imported.ExternalOutcome);
        Assert.Equal(FlowForge.Domain.Executions.WorkflowExecutionStatus.Succeeded, (await db.WorkflowExecutions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, (await db.NodeExecutions.AsNoTracking().SingleAsync()).AttemptCount);
    }
}
