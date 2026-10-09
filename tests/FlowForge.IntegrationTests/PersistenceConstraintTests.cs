using FlowForge.Infrastructure.Persistence;
using Npgsql;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;
namespace FlowForge.IntegrationTests;
[Collection("PostgreSQL")]
public sealed class PersistenceConstraintTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData("owner", "23503")]
    [InlineData("node-owner", "23503")]
    [InlineData("pointer", "23503")]
    [InlineData("endpoint", "23503")]
    [InlineData("second-draft", "23505")]
    [InlineData("version-number", "23505")]
    [InlineData("port", "23505")]
    [InlineData("ordinal", "23514")]
    [InlineData("type", "23514")]
    [InlineData("schema", "23514")]
    [InlineData("position", "23514")]
    [InlineData("credential-type", "23514")]
    [InlineData("delete-owner", "23503")]
    public async Task Raw_sql_cannot_bypass_relational_integrity(string violation, string sqlState)
    {
        var owner = Guid.NewGuid(); var workflow = await CreateAsync(fixture, owner);
        var otherOwner = Guid.NewGuid(); var other = await CreateAsync(fixture, otherOwner);
        var draft = workflow.DraftVersion!;
        var statement = violation switch
        {
            "owner" => "UPDATE public.workflows SET owner_user_id = @otherOwner WHERE id = @workflow",
            "node-owner" => "UPDATE public.workflow_nodes SET owner_user_id = @otherOwner WHERE workflow_version_id = @version",
            "pointer" => "UPDATE public.workflows SET current_published_version_id = @otherVersion WHERE id = @workflow",
            "endpoint" => "UPDATE public.workflow_connections SET target_node_id = @otherNode WHERE workflow_version_id = @version",
            "second-draft" => "UPDATE public.workflow_versions SET status = 1, published_at = NULL WHERE id = @published",
            "version-number" => "UPDATE public.workflow_versions SET version_number = 1 WHERE id = @version",
            "port" => "UPDATE public.workflow_connections SET source_node_id = @condition, source_port = 'true' WHERE workflow_version_id = @version",
            "ordinal" => "UPDATE public.workflow_nodes SET ordinal = -1 WHERE workflow_version_id = @version",
            "type" => "UPDATE public.workflow_nodes SET type = 99 WHERE workflow_version_id = @version",
            "schema" => "UPDATE public.workflow_nodes SET configuration = '{\"schemaVersion\":2}'::jsonb WHERE workflow_version_id = @version",
            "position" => "UPDATE public.workflow_nodes SET position_x = 'NaN'::float8 WHERE workflow_version_id = @version",
            "credential-type" => "UPDATE public.workflow_nodes SET credential_id = @credential WHERE workflow_version_id = @version AND type = 6",
            "delete-owner" => "DELETE FROM public.users WHERE id = @owner",
            _ => throw new ArgumentException("Unknown violation")
        };
        await using var connection = new NpgsqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(statement, connection);
        command.Parameters.AddWithValue("workflow", workflow.Id); command.Parameters.AddWithValue("version", draft.Id);
        command.Parameters.AddWithValue("published", workflow.CurrentPublishedVersionId!.Value);
        command.Parameters.AddWithValue("owner", owner); command.Parameters.AddWithValue("otherOwner", otherOwner);
        command.Parameters.AddWithValue("otherVersion", other.CurrentPublishedVersionId!.Value);
        command.Parameters.AddWithValue("otherNode", other.DraftVersion!.Nodes[0].NodeId);
        command.Parameters.AddWithValue("condition", draft.Nodes.Single(n => n.Type == FlowForge.Domain.Workflows.NodeType.Condition).NodeId);
        command.Parameters.AddWithValue("credential", Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(sqlState, exception.SqlState);
        var reloaded = (await new PostgresWorkflowStore(fixture.Factory).GetAsync(workflow.Id, owner))!;
        Assert.Equal(workflow.Revision, reloaded.Revision);
        Assert.True(WorkflowPersistenceMapper.SameGraph(WorkflowPersistenceMapper.Capture(workflow).Versions[1], WorkflowPersistenceMapper.Capture(reloaded).Versions[1]));
    }
}
