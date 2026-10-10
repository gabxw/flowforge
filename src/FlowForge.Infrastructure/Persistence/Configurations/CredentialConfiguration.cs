using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class CredentialConfiguration : IEntityTypeConfiguration<CredentialRecord>
{
    public void Configure(EntityTypeBuilder<CredentialRecord> b)
    {
        b.ToTable("credentials", t => {
            t.HasCheckConstraint("ck_credential_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid AND revision > 0");
            t.HasCheckConstraint("ck_credential_value", "octet_length(protected_value) BETWEEN 1 AND 16384");
            t.HasCheckConstraint("ck_credential_type", "(type = 1 AND header_name IS NULL) OR (type = 2 AND header_name IS NOT NULL)");
            t.HasCheckConstraint("ck_credential_time", "updated_at >= created_at AND (revoked_at IS NULL OR revoked_at = updated_at)");
        });
        b.HasKey(c => c.Id);
        b.HasAlternateKey(c => new { c.Id, c.OwnerUserId });
        b.Property(c => c.Name).HasMaxLength(120).IsRequired();
        b.Property(c => c.Origin).HasMaxLength(512).IsRequired();
        b.Property(c => c.HeaderName).HasMaxLength(64);
        b.Property(c => c.ProtectedValue).IsRequired();
        b.HasIndex(c => new { c.OwnerUserId, c.CreatedAt, c.Id });
        b.HasOne<TechnicalUserRecord>().WithMany().HasForeignKey(c => c.OwnerUserId).OnDelete(DeleteBehavior.NoAction);
    }
}
