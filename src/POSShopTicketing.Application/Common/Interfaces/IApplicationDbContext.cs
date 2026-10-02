using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.Common.Interfaces;

/// <summary>The Application layer only knows about this abstraction,
/// never EF Core's DbContext directly - keeps Infrastructure
/// swappable.</summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<TeamMember> TeamMembers { get; }
    DbSet<RefreshTokenz> RefreshTokens { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationDepartment> OrganizationDepartments { get; }
    DbSet<OrganizationContact> OrganizationContacts { get; }
    DbSet<AssignmentRule> AssignmentRules { get; }
    DbSet<Mailbox> Mailboxes { get; }
    DbSet<Ticket> Tickets { get; }
    DbSet<TicketMessage> TicketMessages { get; }
    DbSet<Attachment> Attachments { get; }
    DbSet<TicketStatusHistory> TicketStatusHistories { get; }
    DbSet<Tag> Tags { get; }
    DbSet<TicketTag> TicketTags { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SlaPolicy> SlaPolicies { get; }
    DbSet<EmailVerificationOtp> EmailVerificationOtps { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
