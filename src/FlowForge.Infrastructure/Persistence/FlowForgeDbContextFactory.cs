using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace FlowForge.Infrastructure.Persistence;
public sealed class FlowForgeDbContextFactory : IDesignTimeDbContextFactory<FlowForgeDbContext>
{
    public FlowForgeDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("FLOWFORGE_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Defina FLOWFORGE_POSTGRES_CONNECTION_STRING para comandos de migrations.");
        return new FlowForgeDbContext(new DbContextOptionsBuilder<FlowForgeDbContext>().UseNpgsql(connection).Options);
    }
}
