using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class WorkflowConnectionConfiguration : IEntityTypeConfiguration<WorkflowConnectionRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowConnectionRecord> builder)
    {
        builder.ToTable("workflow_connections", table =>
        {
            table.HasCheckConstraint("ck_workflow_connections_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_workflow_connections_ordinal", "ordinal >= 0 AND length(btrim(source_port)) > 0");
        });
        builder.HasKey(connection => connection.Id);
        builder.Property(connection => connection.SourcePort).IsRequired();
        builder.HasOne<WorkflowNodeRecord>().WithMany().HasForeignKey(connection => new { connection.WorkflowVersionId, connection.SourceNodeId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<WorkflowNodeRecord>().WithMany().HasForeignKey(connection => new { connection.WorkflowVersionId, connection.TargetNodeId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(connection => new { connection.WorkflowVersionId, connection.SourceNodeId, connection.SourcePort }).IsUnique();
        builder.HasIndex(connection => new { connection.WorkflowVersionId, connection.Ordinal }).IsUnique();
        builder.HasIndex(connection => new { connection.WorkflowVersionId, connection.TargetNodeId });
    }
}
