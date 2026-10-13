using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSessionRecord>
{
    public void Configure(EntityTypeBuilder<RefreshSessionRecord> b)
    {
        b.ToTable("refresh_sessions", t => t.HasCheckConstraint("ck_refresh_session_expiry", "expires_at > created_at"));
        b.HasKey(x => x.Id);
        b.HasOne<TechnicalUserRecord>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
        b.HasIndex(x => x.ExpiresAt);
    }
}
internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshTokenRecord>
{
    public void Configure(EntityTypeBuilder<RefreshTokenRecord> b)
    {
        b.ToTable("refresh_tokens", t => t.HasCheckConstraint("ck_refresh_token_hash", "octet_length(hash) = 32"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Hash).IsRequired();
        b.HasIndex(x => x.Hash).IsUnique();
        b.HasIndex(x => x.SessionId);
        b.HasOne<RefreshSessionRecord>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}
