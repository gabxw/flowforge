using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class WorkflowNodeConfiguration : IEntityTypeConfiguration<WorkflowNodeRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowNodeRecord> builder)
    {
        builder.ToTable("workflow_nodes", table =>
        {
            table.HasCheckConstraint("ck_workflow_nodes_id", "node_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_workflow_nodes_type", "type BETWEEN 1 AND 6 AND ordinal >= 0");
            table.HasCheckConstraint("ck_workflow_nodes_config", "jsonb_typeof(configuration) = 'object' AND configuration ? 'schemaVersion' AND configuration->>'schemaVersion' = '1'");
            table.HasCheckConstraint("ck_workflow_nodes_credential", "credential_id IS NULL OR (type = 2 AND credential_id <> '00000000-0000-0000-0000-000000000000'::uuid)");
            table.HasCheckConstraint("ck_workflow_nodes_position", "position_x > '-Infinity'::float8 AND position_x < 'Infinity'::float8 AND position_y > '-Infinity'::float8 AND position_y < 'Infinity'::float8");
        });
        builder.HasKey(node => new { node.WorkflowVersionId, node.NodeId });
        builder.Property(node => node.Configuration).HasColumnType("jsonb").IsRequired();
        builder.HasOne<WorkflowVersionRecord>().WithMany().HasForeignKey(node => new { node.WorkflowVersionId, node.WorkflowId, node.OwnerUserId })
            .HasPrincipalKey(version => new { version.Id, version.WorkflowId, version.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CredentialRecord>().WithMany().HasForeignKey(node => new { node.CredentialId, node.OwnerUserId })
            .HasPrincipalKey(c => new { c.Id, c.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(node => new { node.WorkflowVersionId, node.Ordinal }).IsUnique();
    }
}
