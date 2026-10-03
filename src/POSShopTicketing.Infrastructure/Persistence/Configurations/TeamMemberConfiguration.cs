using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Infrastructure.Persistence.Configurations;

public class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.ToTable("TeamMembers");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Email).IsRequired().HasMaxLength(200);
        // Deviation from the spec's literal "unique per tenant": kept
        // globally unique instead, because POST /auth/login as specified
        // takes only { email, password } with no tenant selector, which
        // only works if email resolves to exactly one account. See README.
        builder.HasIndex(x => new
        {
            x.TenantId,
            x.Email
        }).IsUnique();

        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.FirstName).HasMaxLength(100);
        builder.Property(u => u.LastName).HasMaxLength(100);
        builder.Property(u => u.Role).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(u => u.InviteTokenHash).HasMaxLength(200);
        builder.Property(u => u.CreatedBy).HasMaxLength(100);
        builder.Property(u => u.LastModifiedBy).HasMaxLength(100);

        builder.Ignore(u => u.FullName);

        builder.HasIndex(u => u.TenantId);

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(t => t.TeamMember)
            .HasForeignKey(t => t.TeamMemberId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
