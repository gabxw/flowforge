using FlowForge.Application.Users;
using Microsoft.EntityFrameworkCore;
namespace FlowForge.Infrastructure.Persistence;
public sealed class PostgresTechnicalUserStore(IDbContextFactory<FlowForgeDbContext> factory) : ITechnicalUserStore
{
    public async Task EnsureExistsAsync(Guid id, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("O proprietário deve ter identidade.", nameof(id));
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var timestamp = WorkflowPersistenceMapper.Normalize(createdAt);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.users (id, created_at) VALUES ({id}, {timestamp}) ON CONFLICT (id) DO NOTHING", cancellationToken);
    }
}
