using FlowForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static FlowForge.IntegrationTests.WorkflowStoreFixtures;
namespace FlowForge.IntegrationTests;
[Collection("PostgreSQL")]
public sealed class TechnicalUserStoreTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_ensure_is_idempotent_and_preserves_initial_created_at()
    {
        var id = Guid.NewGuid(); var store = new PostgresTechnicalUserStore(fixture.Factory);
        await store.EnsureExistsAsync(id, Start.AddTicks(7));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.EnsureExistsAsync(id, Start.AddDays(1))));
        await using var context = await fixture.Factory.CreateDbContextAsync();
        var actual = Assert.Single(await context.Users.AsNoTracking().Where(u => u.Id == id).ToArrayAsync());
        Assert.Equal(Start, actual.CreatedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnsureExistsAsync(Guid.Empty, Start));
    }
}
