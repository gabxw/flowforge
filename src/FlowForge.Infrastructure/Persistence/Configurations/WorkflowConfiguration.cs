using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class WorkflowConfiguration : IEntityTypeConfiguration<WorkflowRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowRecord> builder)
    {
        builder.ToTable("workflows", table =>
        {
            table.HasCheckConstraint("ck_workflows_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_workflows_name", "length(btrim(name)) BETWEEN 1 AND 200");
            table.HasCheckConstraint("ck_workflows_revision", "revision >= 0");
            table.HasCheckConstraint("ck_workflows_dates", "updated_at >= created_at AND (archived_at IS NULL OR archived_at = updated_at)");
        });
        builder.HasKey(workflow => workflow.Id);
        builder.HasAlternateKey(workflow => new { workflow.Id, workflow.OwnerUserId });
        builder.Property(workflow => workflow.Name).HasMaxLength(200).IsRequired();
        builder.Property(workflow => workflow.Description).HasMaxLength(2000);
        builder.Property(workflow => workflow.Revision).IsConcurrencyToken();
        builder.HasOne<TechnicalUserRecord>().WithMany().HasForeignKey(workflow => workflow.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<WorkflowVersionRecord>().WithMany()
            .HasForeignKey(workflow => new { workflow.CurrentPublishedVersionId, workflow.Id, workflow.OwnerUserId })
            .HasPrincipalKey(version => new { version.Id, version.WorkflowId, version.OwnerUserId })
            .OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(workflow => new { workflow.OwnerUserId, workflow.UpdatedAt, workflow.Id }).IsDescending(false, true, true);
    }
}
