using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Configurations;

internal sealed class WorkflowExecutionConfiguration : IEntityTypeConfiguration<WorkflowExecutionRecord>
{
    public void Configure(EntityTypeBuilder<WorkflowExecutionRecord> builder)
    {
        builder.ToTable("workflow_executions", t =>
        {
            t.HasCheckConstraint("ck_execution_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_execution_state", "(status = 1 AND started_at IS NULL AND finished_at IS NULL AND error_code IS NULL) OR (status = 2 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NULL AND error_code IS NULL) OR (status = 4 AND started_at IS NOT NULL AND started_at >= created_at AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code = 1)");
        });
        builder.HasKey(e => e.Id);
        builder.HasOne<WorkflowVersionRecord>().WithMany()
            .HasForeignKey(e => new { e.WorkflowVersionId, e.WorkflowId, e.OwnerUserId })
            .HasPrincipalKey(v => new { v.Id, v.WorkflowId, v.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(e => new { e.OwnerUserId, e.CreatedAt, e.Id });
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessageRecord>
{
    public void Configure(EntityTypeBuilder<OutboxMessageRecord> builder)
    {
        builder.ToTable("outbox_messages", t =>
        {
            t.HasCheckConstraint("ck_outbox_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND correlation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_outbox_contract", "contract_version = 1 AND publish_attempts >= 0");
            t.HasCheckConstraint("ck_outbox_lease", "(claim_token IS NULL) = (claim_until IS NULL) AND (published_at IS NULL OR (claim_token IS NULL AND published_at >= created_at))");
        });
        builder.HasKey(o => o.Id);
        builder.HasAlternateKey(o => new { o.Id, o.ExecutionId });
        builder.HasIndex(o => o.ExecutionId).IsUnique();
        builder.HasIndex(o => new { o.AvailableAt, o.Id }).HasFilter("published_at IS NULL");
        builder.HasOne<WorkflowExecutionRecord>().WithMany().HasForeignKey(o => o.ExecutionId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessageRecord>
{
    public void Configure(EntityTypeBuilder<InboxMessageRecord> builder)
    {
        builder.ToTable("inbox_messages", t =>
        {
            t.HasCheckConstraint("ck_inbox_state", "claim_attempts > 0 AND ((completed_at IS NULL AND claim_token IS NOT NULL AND claim_until IS NOT NULL) OR (completed_at IS NOT NULL AND completed_at >= received_at AND claim_token IS NULL AND claim_until IS NULL))");
        });
        builder.HasKey(i => i.MessageId);
        builder.HasIndex(i => i.ExecutionId).IsUnique();
        builder.HasOne<OutboxMessageRecord>().WithMany().HasForeignKey(i => new { i.MessageId, i.ExecutionId })
            .HasPrincipalKey(o => new { o.Id, o.ExecutionId }).OnDelete(DeleteBehavior.NoAction);
    }
}
