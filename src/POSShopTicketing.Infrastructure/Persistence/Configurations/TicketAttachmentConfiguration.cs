using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Infrastructure.Persistence.Configurations;

public class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> builder)
    {
        builder.ToTable("TicketAttachments");

        builder.HasKey(a => a.Id);

        builder.HasIndex(a => new
        {
            a.TenantId,
            a.TicketId
        });

        builder.Property(a => a.FileName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.ContentType)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(a => a.StorageKey)
            .IsRequired()
            .HasMaxLength(300);

        builder.Property(a => a.ContentId)
            .HasMaxLength(255);

        builder.Property(a => a.SizeBytes)
            .IsRequired();

        builder.HasOne(a => a.TicketMessage)
            .WithMany()
            .HasForeignKey(a => a.TicketMessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}