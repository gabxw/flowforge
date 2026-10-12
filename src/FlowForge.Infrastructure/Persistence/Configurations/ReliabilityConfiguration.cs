
using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class NodeAttemptConfiguration : IEntityTypeConfiguration<NodeAttemptRecord>
{
    public void Configure(EntityTypeBuilder<NodeAttemptRecord> b)
    {
        b.ToTable("node_execution_attempts", t => {
            t.HasCheckConstraint("ck_attempt_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND number > 0");
            t.HasCheckConstraint("ck_attempt_state", "(status = 1 AND finished_at IS NULL AND error_code IS NULL) OR (status IN (2, 4) AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NULL) OR (status IN (3, 5) AND finished_at IS NOT NULL AND finished_at >= started_at AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 20)");
            t.HasCheckConstraint("ck_attempt_metadata", "external_outcome BETWEEN 1 AND 5 AND (http_status_code IS NULL OR http_status_code BETWEEN 100 AND 599) AND (credential_revision_used IS NULL OR credential_revision_used > 0) AND (dispatched_at IS NULL OR (dispatched_at >= started_at AND (finished_at IS NULL OR dispatched_at <= finished_at))) AND (retry_at IS NULL OR (finished_at IS NOT NULL AND retry_at > finished_at))");
        });
        b.HasKey(a => a.Id);
        b.HasIndex(a => new { a.NodeExecutionId, a.Number }).IsUnique();
        b.HasIndex(a => new { a.ExecutionId, a.StartedAt, a.Id });
        b.HasOne<NodeExecutionRecord>().WithMany().HasForeignKey(a => new { a.NodeExecutionId, a.ExecutionId })
            .HasPrincipalKey(n => new { n.Id, n.ExecutionId }).OnDelete(DeleteBehavior.NoAction);
    }
}
internal sealed class ExecutionRedriveConfiguration : IEntityTypeConfiguration<ExecutionRedriveRecord>
{
    public void Configure(EntityTypeBuilder<ExecutionRedriveRecord> b)
    {
        b.ToTable("execution_redrives", t => t.HasCheckConstraint("ck_redrive", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND expected_publish_attempts >= 0"));
        b.HasKey(r => r.Id);
        b.HasIndex(r => new { r.MessageId, r.ExpectedPublishAttempts }).IsUnique();
        b.HasIndex(r => new { r.ExecutionId, r.RequestedAt });
        b.HasOne<OutboxMessageRecord>().WithMany().HasForeignKey(r => new { r.MessageId, r.ExecutionId })
            .HasPrincipalKey(o => new { o.Id, o.ExecutionId }).OnDelete(DeleteBehavior.NoAction);
    }
}


internal sealed class ExecutionReplayConfiguration : IEntityTypeConfiguration<ExecutionReplayRecord>
{
    public void Configure(EntityTypeBuilder<ExecutionReplayRecord> b)
    {
        b.ToTable("execution_replays", t => t.HasCheckConstraint("ck_replay", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND original_execution_id <> new_execution_id AND key_digest ~ '^[0-9A-F]{64}$'"));
        b.HasKey(r => r.Id);
        b.HasIndex(r => new { r.OriginalExecutionId, r.KeyDigest }).IsUnique();
        b.HasIndex(r => r.NewExecutionId).IsUnique();
        b.HasOne<WorkflowExecutionRecord>().WithMany().HasForeignKey(r => new { r.OriginalExecutionId, r.WorkflowId, r.OwnerUserId })
            .HasPrincipalKey(e => new { e.Id, e.WorkflowId, e.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<WorkflowExecutionRecord>().WithMany().HasForeignKey(r => new { r.NewExecutionId, r.WorkflowId, r.OwnerUserId })
            .HasPrincipalKey(e => new { e.Id, e.WorkflowId, e.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
    }
}
