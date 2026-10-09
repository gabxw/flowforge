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
    }
}
