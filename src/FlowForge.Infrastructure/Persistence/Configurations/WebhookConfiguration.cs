using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class WebhookEndpointConfiguration : IEntityTypeConfiguration<WebhookEndpointRecord>
{
    public void Configure(EntityTypeBuilder<WebhookEndpointRecord> b)
    {
        b.ToTable("webhook_endpoints", t => {
            t.HasCheckConstraint("ck_webhook_endpoint", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND octet_length(secret_hash) = 32 AND (rotated_at IS NULL OR rotated_at >= created_at)");
        });
        b.HasKey(e => e.Id);
        b.HasAlternateKey(e => new { e.Id, e.OwnerUserId });
        b.HasAlternateKey(e => new { e.Id, e.WorkflowId, e.OwnerUserId });
        b.HasIndex(e => e.WorkflowId).IsUnique();
        b.HasOne<WorkflowRecord>().WithMany().HasForeignKey(e => new { e.WorkflowId, e.OwnerUserId })
            .HasPrincipalKey(w => new { w.Id, w.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
    }
}
internal sealed class WebhookIdempotencyConfiguration : IEntityTypeConfiguration<WebhookIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<WebhookIdempotencyRecord> b)
    {
        b.ToTable("webhook_idempotency", t => {
            t.HasCheckConstraint("ck_webhook_digests", "key_digest ~ '^[0-9A-F]{64}$' AND request_digest ~ '^[0-9A-F]{64}$'");
            t.HasCheckConstraint("ck_webhook_retention", "expires_at > created_at");
        });
        // Endpoint é globalmente único; a FK composta fixa seu proprietário.
        b.HasKey(e => new { e.EndpointId, e.KeyDigest });
        b.Property(e => e.KeyDigest).HasMaxLength(64).IsRequired();
        b.Property(e => e.RequestDigest).HasMaxLength(64).IsRequired();
        b.HasIndex(e => e.ExpiresAt);
        b.HasOne<WebhookEndpointRecord>().WithMany().HasForeignKey(e => new { e.EndpointId, e.WorkflowId, e.OwnerUserId })
            .HasPrincipalKey(e => new { e.Id, e.WorkflowId, e.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<WorkflowExecutionRecord>().WithMany().HasForeignKey(e => new { e.ExecutionId, e.WorkflowId, e.OwnerUserId })
            .HasPrincipalKey(e => new { e.Id, e.WorkflowId, e.OwnerUserId }).OnDelete(DeleteBehavior.NoAction);
    }
}
