using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Infrastructure.Persistence.Configurations;

// =====================================================================
//  NEW FILE: Infrastructure/Persistence/Configurations/TicketThreadConfiguration.cs
//
//  1) FIRST add this property to your Ticket entity (Domain/Entities/Ticket.cs), next to MailboxId:
//
//        /// <summary>
//        /// The ticket's ONE mail thread: the normalized Message-ID (no angle brackets, lowercase) of the first email of
//        /// the thread. Null until a thread exists - a ticket created by hand has none until the first message is
//        /// emailed to the customer. A customer's email whose threadId matches a ticket here is a REPLY to it;
//        /// a threadId that matches nothing raises a NEW ticket.
//        /// </summary>
//        public string? ThreadId { get; set; }
//
//  2) This file adds the column settings and the index WITHOUT touching your existing Ticket configuration
//     (EF Core accepts several IEntityTypeConfiguration classes for one entity, and your
//     ApplyConfigurationsFromAssembly picks this one up automatically).
//
//  3) Then create the migration - see the notes in the chat reply.
// =====================================================================
public class TicketThreadConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.Property(t => t.ThreadId).HasMaxLength(500);

        // One ticket per thread, per workspace. Tickets without a thread yet (NULL) are not part of the index.
        builder.HasIndex(t => new { t.TenantId, t.ThreadId })
            .IsUnique()
            .HasFilter("\"ThreadId\" IS NOT NULL");   // PostgreSQL syntax
    }
}