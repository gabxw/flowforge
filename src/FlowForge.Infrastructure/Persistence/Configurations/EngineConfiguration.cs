using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class NodeExecutionConfiguration : IEntityTypeConfiguration<NodeExecutionRecord>
{
    public void Configure(EntityTypeBuilder<NodeExecutionRecord> b)
    {
        b.ToTable("node_executions", t =>
        {
            t.HasCheckConstraint("ck_node_execution_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND ordinal >= 0");
            t.HasCheckConstraint("ck_node_credential_revision", "credential_revision_used IS NULL OR (credential_revision_used > 0 AND attempt_count > 0 AND status IN (2, 3, 4, 7))");
            t.HasCheckConstraint("ck_node_execution_state", "(status = 1 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 2 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NULL AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 3 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NOT NULL AND error_code IS NULL) OR (status = 4 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NOT NULL AND error_code BETWEEN 1 AND 18) OR (status = 6 AND attempt_count = 0 AND started_at IS NULL AND finished_at IS NOT NULL AND input_summary IS NULL AND output_summary IS NULL AND error_code IS NULL) OR (status = 7 AND attempt_count > 0 AND started_at IS NOT NULL AND finished_at IS NOT NULL AND finished_at >= started_at AND input_summary IS NOT NULL AND output_summary IS NULL AND error_code IS NULL)");
        });
        b.HasKey(n => n.Id);
        b.HasAlternateKey(n => new { n.Id, n.ExecutionId });
        b.HasIndex(n => new { n.ExecutionId, n.NodeId }).IsUnique();
        b.HasIndex(n => new { n.ExecutionId, n.Ordinal }).IsUnique();
        b.Property(n => n.InputSummary).HasColumnType("jsonb");
        b.Property(n => n.OutputSummary).HasColumnType("jsonb");
        b.HasOne<WorkflowExecutionRecord>().WithMany().HasForeignKey(n => new { n.ExecutionId, n.WorkflowVersionId })
            .HasPrincipalKey(e => new { e.Id, e.WorkflowVersionId }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<WorkflowNodeRecord>().WithMany().HasForeignKey(n => new { n.WorkflowVersionId, n.NodeId }).OnDelete(DeleteBehavior.NoAction);
    }
}
internal sealed class ExecutionLogConfiguration : IEntityTypeConfiguration<ExecutionLogRecord>
{
    public void Configure(EntityTypeBuilder<ExecutionLogRecord> b)
    {
        b.ToTable("execution_logs", t => t.HasCheckConstraint("ck_execution_log_content", "event_code = 1 AND message_byte_length BETWEEN 1 AND 8000 AND octet_length(message_protected) BETWEEN 1 AND 16384"));
        b.HasKey(l => l.Id);
        b.HasIndex(l => l.NodeExecutionId).IsUnique(); // Um evento lógico Log; replay não duplica a mensagem.
        b.HasIndex(l => new { l.ExecutionId, l.CreatedAt, l.Id });
        b.HasOne<NodeExecutionRecord>().WithMany().HasForeignKey(l => new { l.NodeExecutionId, l.ExecutionId })
            .HasPrincipalKey(n => new { n.Id, n.ExecutionId }).OnDelete(DeleteBehavior.NoAction);
    }
}
