using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class WorkflowVersionConfiguration : IEntityTypeConfiguration<WorkflowVersionRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowVersionRecord> builder)
    {
        builder.ToTable("workflow_versions", table =>
        {
            table.HasCheckConstraint("ck_workflow_versions_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_workflow_versions_revision", "revision >= 0 AND version_number > 0");
            table.HasCheckConstraint("ck_workflow_versions_status", "(status = 1 AND published_at IS NULL) OR (status = 2 AND published_at IS NOT NULL AND published_at >= created_at)");
        });
        builder.HasKey(version => version.Id);
        builder.HasAlternateKey(version => new { version.Id, version.WorkflowId, version.OwnerUserId });
        builder.HasOne<WorkflowRecord>().WithMany().HasForeignKey(version => new { version.WorkflowId, version.OwnerUserId })
            .HasPrincipalKey(workflow => new { workflow.Id, workflow.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(version => new { version.WorkflowId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => version.WorkflowId).IsUnique().HasFilter("status = 1");
    }
}
