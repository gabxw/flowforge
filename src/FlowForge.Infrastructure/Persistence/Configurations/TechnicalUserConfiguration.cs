using FlowForge.Infrastructure.Persistence.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace FlowForge.Infrastructure.Persistence.Configurations;
internal sealed class TechnicalUserConfiguration : IEntityTypeConfiguration<TechnicalUserRecord>
{
    public void Configure(EntityTypeBuilder<TechnicalUserRecord> builder)
    {
        builder.ToTable("users", table => table.HasCheckConstraint("ck_users_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid"));
        builder.HasKey(user => user.Id);
        builder.Property(x => x.Email).HasMaxLength(254);
        builder.Property(x => x.NormalizedEmail).HasMaxLength(254);
        builder.Property(x => x.PasswordHash).HasMaxLength(512);
        builder.Property(x => x.Role).HasConversion<string>().HasMaxLength(16).HasDefaultValue(FlowForge.Domain.Users.UserRole.Owner);
        builder.Property(x => x.Disabled).HasDefaultValue(false);
        builder.Property(x => x.FailedSignIns).HasDefaultValue(0);
        builder.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("normalized_email IS NOT NULL");
        builder.ToTable("users", t => {
            t.HasCheckConstraint("ck_users_role", "role IN ('Owner', 'Admin')");
            t.HasCheckConstraint("ck_users_auth_pair", "(email IS NULL AND normalized_email IS NULL AND password_hash IS NULL) OR (email IS NOT NULL AND normalized_email IS NOT NULL AND password_hash IS NOT NULL)");
            t.HasCheckConstraint("ck_users_failed_sign_ins", "failed_sign_ins BETWEEN 0 AND 5");
        });
    }
}
