using FlowForge.Domain.Workflows;
using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;
namespace FlowForge.IntegrationTests;
public sealed class PersistenceDesignTests
{
    [Fact]
    public void Six_type_fixture_and_microsecond_normalization_form_a_valid_domain_snapshot()
    {
        var workflow = Definition(Guid.NewGuid());
        var snapshot = WorkflowPersistenceMapper.Capture(workflow);
        Assert.Equal(6, snapshot.Versions[0].Nodes.Count);
        Assert.Equal(6, snapshot.Versions[0].Connections.Count);
        Assert.Equal(0, snapshot.CreatedAt.UtcTicks % 10);
        Assert.Equal(2, Workflow.Restore(snapshot).Versions.Count);
    }
    [Fact]
    public void Migration_model_is_coherent_without_opening_a_database()
    {
        using var context = new FlowForgeDbContext(new DbContextOptionsBuilder<FlowForgeDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=flowforge_design").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(6, context.Database.GetMigrations().Count());
    }
}
