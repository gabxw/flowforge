using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.RegularExpressions;
namespace FlowForge.Infrastructure.Persistence;
public sealed class FlowForgeDbContext(DbContextOptions<FlowForgeDbContext> options) : DbContext(options)
{
    internal DbSet<TechnicalUserRecord> Users => Set<TechnicalUserRecord>();
    internal DbSet<WorkflowRecord> Workflows => Set<WorkflowRecord>();
    internal DbSet<WorkflowVersionRecord> WorkflowVersions => Set<WorkflowVersionRecord>();
    internal DbSet<WorkflowNodeRecord> WorkflowNodes => Set<WorkflowNodeRecord>();
    internal DbSet<WorkflowConnectionRecord> WorkflowConnections => Set<WorkflowConnectionRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowForgeDbContext).Assembly);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Snake(property.Name));
                if (property.ClrType == typeof(Guid)) property.ValueGenerated = ValueGenerated.Never;
            }
            foreach (var key in entity.GetKeys()) key.SetName((key.IsPrimaryKey() ? "pk_" : "ak_") + entity.GetTableName() +
                (key.IsPrimaryKey() ? "" : "_" + string.Join("_", key.Properties.Select(p => Snake(p.Name)))));
            foreach (var index in entity.GetIndexes()) index.SetDatabaseName("ix_" + entity.GetTableName() + "_" +
                string.Join("_", index.Properties.Select(p => Snake(p.Name).Replace("workflow_version_id", "version_id"))));
            foreach (var foreignKey in entity.GetForeignKeys()) foreignKey.SetConstraintName("fk_" + entity.GetTableName() + "_" +
                string.Join("_", foreignKey.Properties.Select(p => Snake(p.Name))));
        }
    }
    private static string Snake(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
