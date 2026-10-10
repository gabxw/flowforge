using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace FlowForge.IntegrationTests;

[Collection("PostgreSQL")]
public sealed class PersistenceSchemaTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Migration_creates_the_current_application_tables()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
            ORDER BY table_name
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Equal(["inbox_messages", "outbox_messages", "users", "workflow_connections", "workflow_executions", "workflow_nodes", "workflow_versions", "workflows"], tables);
    }

    [Fact]
    public async Task Migration_can_be_reapplied_without_model_drift()
    {
        await using var context = await fixture.Factory.CreateDbContextAsync();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Equal(2, applied.Length);
        await context.Database.MigrateAsync();
        Assert.Equal(applied, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges());
    }
}
