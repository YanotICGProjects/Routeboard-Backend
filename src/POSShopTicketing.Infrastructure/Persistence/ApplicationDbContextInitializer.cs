using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Infrastructure.Persistence;

/// <summary>
/// Applies pending migrations and, on an empty database, seeds a
/// PlatformSuperAdmin plus one fully fleshed-out demo Tenant (team,
/// organizations, mailbox, SLA policies, assignment rule, and tickets in
/// several lifecycle states including one Unverified) - enough to
/// exercise every endpoint immediately after first run.
/// </summary>
public class ApplicationDbContextInitializer
{
    // Dev-only convenience credentials - change or remove before any
    // real deployment (see README).
    private const string SeedPassword = "Passw0rd!123";
    private const string SeedMailboxWebhookSecret = "dev-webhook-secret-do-not-use-in-production";

    private readonly ILogger<ApplicationDbContextInitializer> _logger;
    private readonly ApplicationDbContext _context;
    private readonly IPasswordHasherService _passwordHasher;
    private readonly ITicketNumberGenerator _ticketNumberGenerator;

    public ApplicationDbContextInitializer(
        ILogger<ApplicationDbContextInitializer> logger,
        ApplicationDbContext context,
        IPasswordHasherService passwordHasher,
        ITicketNumberGenerator ticketNumberGenerator)
    {
        _logger = logger;
        _context = context;
        _passwordHasher = passwordHasher;
        _ticketNumberGenerator = ticketNumberGenerator;
    }

    public async Task InitialiseAsync()
    {
        try
        {
            if (_context.Database.IsRelational())
            {
                await _context.Database.MigrateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while migrating the Routeboard database.");
            throw;
        }
    }

    public async Task SeedAsync()
    {
        try
        {
            await EnsurePlatformTenantRowAsync();
            await SeedPlatformSuperAdminAsync();
            await SeedDemoTenantAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding the POSShopTicketing database.");
            throw;
        }
    }

    /// <summary>
    /// The actual bug this method fixes: TeamMember.TenantId is a plain
    /// (non-nullable) Guid - Guid.Empty is the deliberate sentinel for
    /// "no tenant" (PlatformSuperAdmin), documented on that property and
    /// on ITenantScoped. What wasn't accounted for is that EF Core's own
    /// convention still auto-discovers a required foreign key from
    /// TeamMember.TenantId to Tenants.Id (matched via the
    /// Tenant.TeamMembers collection navigation - nothing in
    /// TeamMemberConfiguration overrides this), so the database enforces
    /// that every TeamMembers.TenantId value must reference a real row
    /// in Tenants. Guid.Empty never did, so inserting the
    /// PlatformSuperAdmin below violated that constraint - Postgres
    /// reports it as a foreign key violation, which reads a lot like a
    /// "cannot be null" error if you're not expecting an FK check at all
    /// on what looks like a sentinel value.
    ///
    /// The fix: make Guid.Empty a real, reserved Tenant row instead of
    /// fighting the FK. This keeps referential integrity meaningful for
    /// every ordinary tenant (a TeamMember can never dangle off a
    /// deleted/nonexistent Tenant) while still giving PlatformSuperAdmin
    /// a well-defined "home" row that satisfies the constraint. It's
    /// filtered out of GetTenantsQuery so it never shows up as if it
    /// were a real customer tenant.
    /// </summary>
    private async Task EnsurePlatformTenantRowAsync()
    {
        var exists = await _context.Tenants.AnyAsync(t => t.Id == Guid.Empty);
        if (exists)
        {
            return;
        }

        _context.Tenants.Add(new Tenant
        {
            Id = Guid.Empty,
            Name = "Platform",
            Slug = "platform",
            TicketPrefix = "PLAT",
            Plan = TenantPlan.Enterprise,
            Status = TenantStatus.Active
        });

        await _context.SaveChangesAsync();
    }

    private async Task SeedPlatformSuperAdminAsync()
    {
        if (await _context.TeamMembers.IgnoreQueryFilters().AnyAsync(u => u.Role == TeamMemberRole.PlatformSuperAdmin))
        {
            return;
        }

        _context.TeamMembers.Add(new TeamMember
        {
            TenantId = Guid.Empty,
            Email = "superadmin@posshopticketing.platform",
            PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Platform",
            LastName = "SuperAdmin",
            Role = TeamMemberRole.PlatformSuperAdmin,
            Status = TeamMemberStatus.Active
        });

        await _context.SaveChangesAsync();
    }

    private async Task SeedDemoTenantAsync()
    {
        if (await _context.Tenants.AnyAsync())
        {
            return;
        }

        var tenant = new Tenant
        {
            Name = "Acme Support",
            Slug = "acme-support",
            TicketPrefix = "ACME",
            Plan = TenantPlan.Growth,
            Status = TenantStatus.Active
        };
        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        var owner = new TeamMember
        {
            TenantId = tenant.Id, Email = "owner@acme-support.test", PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Olu", LastName = "Owner", Role = TeamMemberRole.Owner, Status = TeamMemberStatus.Active
        };
        var admin = new TeamMember
        {
            TenantId = tenant.Id, Email = "admin@acme-support.test", PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Ada", LastName = "Admin", Role = TeamMemberRole.Admin, Status = TeamMemberStatus.Active
        };
        var manager = new TeamMember
        {
            TenantId = tenant.Id, Email = "manager@acme-support.test", PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Mira", LastName = "Manager", Role = TeamMemberRole.Manager, Status = TeamMemberStatus.Active
        };
        var agent1 = new TeamMember
        {
            TenantId = tenant.Id, Email = "agent1@acme-support.test", PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Tolu", LastName = "Agent", Role = TeamMemberRole.Agent, Status = TeamMemberStatus.Active
        };
        var agent2 = new TeamMember
        {
            TenantId = tenant.Id, Email = "agent2@acme-support.test", PasswordHash = _passwordHasher.Hash(SeedPassword),
            FirstName = "Zainab", LastName = "Agent", Role = TeamMemberRole.Agent, Status = TeamMemberStatus.Active
        };
        _context.TeamMembers.AddRange(owner, admin, manager, agent1, agent2);
        await _context.SaveChangesAsync();

        var mailbox = new Mailbox
        {
            TenantId = tenant.Id,
            EmailAddress = "support@acme-support.posshopticketing.app",
            Provider = MailboxProvider.SendGridInboundParse,
            WebhookSecret = SeedMailboxWebhookSecret,
            IsVerified = true,
            IsDefault = true
        };
        _context.Mailboxes.Add(mailbox);

        _context.SlaPolicies.AddRange(
            new SlaPolicy { TenantId = tenant.Id, Priority = TicketPriority.Urgent, FirstResponseTargetMinutes = 30, ResolutionTargetMinutes = 240 },
            new SlaPolicy { TenantId = tenant.Id, Priority = TicketPriority.High, FirstResponseTargetMinutes = 60, ResolutionTargetMinutes = 480 },
            new SlaPolicy { TenantId = tenant.Id, Priority = TicketPriority.Medium, FirstResponseTargetMinutes = 240, ResolutionTargetMinutes = 1440 },
            new SlaPolicy { TenantId = tenant.Id, Priority = TicketPriority.Low, FirstResponseTargetMinutes = 480, ResolutionTargetMinutes = 4320 });

        var contoso = new Organization { TenantId = tenant.Id, Name = "Contoso Ltd", Status = OrganizationStatus.Active };
        var fabrikam = new Organization { TenantId = tenant.Id, Name = "Fabrikam Inc", Status = OrganizationStatus.Active };
        _context.Organizations.AddRange(contoso, fabrikam);
        await _context.SaveChangesAsync();

        var contosoIt = new OrganizationDepartment { TenantId = tenant.Id, OrganizationId = contoso.Id, Name = "IT" };
        var contosoSales = new OrganizationDepartment { TenantId = tenant.Id, OrganizationId = contoso.Id, Name = "Sales" };
        _context.OrganizationDepartments.AddRange(contosoIt, contosoSales);
        await _context.SaveChangesAsync();

        var contosoItMember = new OrganizationContact
        {
            TenantId = tenant.Id, OrganizationId = contoso.Id,
            OrganizationDepartmentId = contosoIt.Id,
            LastName = "Priya Kapoor", Email = "priya.kapoor@contoso.example", Phone = "+1-555-0101"
        };
        var contosoSalesMember = new OrganizationContact
        {
            TenantId = tenant.Id, OrganizationId = contoso.Id,
            OrganizationDepartmentId = contosoSales.Id,
            LastName = "Diego Alvarez", Email = "diego.alvarez@contoso.example"
        };
        var fabrikamMember = new OrganizationContact
        {
            TenantId = tenant.Id, OrganizationId = fabrikam.Id,
            LastName = "Grace Chen", Email = "grace.chen@fabrikam.example"
        };
        _context.OrganizationContacts.AddRange(contosoItMember, contosoSalesMember, fabrikamMember);
        await _context.SaveChangesAsync();

        // Assignment Preset: Contoso's IT team always routes to agent1.
        _context.AssignmentRules.Add(new AssignmentRule
        {
            TenantId = tenant.Id,
            ScopeType = AssignmentScopeType.OrganizationDepartment,
            ScopeId = contosoIt.Id,
            AssignedToTeamMemberId = agent1.Id,
            PriorityOrder = 0,
            IsActive = true
        });

        var now = DateTime.UtcNow;

        await AddTicketAsync(tenant, contoso.Id, contosoIt.Id, contosoItMember.Id, mailbox.Id,
            "Card reader offline at till 2", contosoItMember.Email, contosoItMember.LastName,
            "Card reader shows a red light and won't take contactless payments.",
            TicketStatus.New, TicketPriority.High, agent1.Id, now, resolvedAt: null, closedAt: null, firstResponseAt: null);

        await AddTicketAsync(tenant, contoso.Id, contosoSales.Id, contosoSalesMember.Id, mailbox.Id,
            "Question about bulk order pricing", contosoSalesMember.Email, contosoSalesMember.LastName,
            "Do you offer a discount for orders over 500 units?",
            TicketStatus.Pending, TicketPriority.Medium, agent2.Id, now, resolvedAt: null, closedAt: null,
            firstResponseAt: now.AddHours(-2));

        await AddTicketAsync(tenant, fabrikam.Id, null, fabrikamMember.Id, mailbox.Id,
            "Password reset request", fabrikamMember.Email, fabrikamMember.LastName,
            "I'm locked out of my account after too many failed login attempts.",
            TicketStatus.Resolved, TicketPriority.Low, agent2.Id, now.AddDays(-3),
            resolvedAt: now.AddDays(-2), closedAt: null, firstResponseAt: now.AddDays(-3).AddHours(1));

        // An Unverified ticket - a message from an address that isn't a
        // registered Organization Member yet, sitting in the triage
        // queue for a Manager to Register/Link/Anonymize/Reject.
        await AddTicketAsync(tenant, null, null, null, mailbox.Id,
            "Do you sell replacement parts?", "unknown.sender@example.com", "Unknown Sender",
            "Hi, I found your support address online - do you sell replacement receipt paper rolls?",
            TicketStatus.Unverified, TicketPriority.Medium, null, now.AddHours(-4),
            resolvedAt: null, closedAt: null, firstResponseAt: null);
    }

    private async Task AddTicketAsync(
        Tenant tenant, Guid? organizationId, Guid? organizationTeamId, Guid? organizationMemberId, Guid mailboxId,
        string subject, string senderEmail, string senderName, string firstMessageBody,
        TicketStatus status, TicketPriority priority, Guid? assignedToTeamMemberId, DateTime createdAt,
        DateTime? resolvedAt, DateTime? closedAt, DateTime? firstResponseAt)
    {
        var ticket = new Ticket
        {
            TenantId = tenant.Id,
            TicketNumber = await _ticketNumberGenerator.NextAsync(tenant.TicketPrefix, CancellationToken.None),
            OrganizationId = organizationId,
            OrganizationDepartmentId = organizationTeamId,
            OrganizationContactId = organizationMemberId,
            RawSenderEmail = senderEmail,
            MailboxId = mailboxId,
            Subject = subject,
            Status = status,
            Priority = priority,
            Source = TicketSource.Email,
            AssignedToTeamMemberId = assignedToTeamMemberId,
            ResolvedAt = resolvedAt,
            ClosedAt = closedAt,
            FirstResponseAt = firstResponseAt
        };

        ticket.Messages.Add(new TicketMessage
        {
            TenantId = tenant.Id,
            TicketId = ticket.Id,
            Direction = MessageDirection.Inbound,
            AuthorType = MessageAuthorType.OrganizationContact,
            AuthorEmail = senderEmail,
            AuthorName = senderName,
            Body = firstMessageBody,
            MessageId = $"<seed-{ticket.Id}@acme-support.example>"
        });

        ticket.StatusHistory.Add(new TicketStatusHistory
        {
            TenantId = tenant.Id,
            TicketId = ticket.Id,
            FromStatus = TicketStatus.Unverified,
            ToStatus = status,
            ChangedAt = createdAt,
            Note = "Seed data"
        });

        _context.Tickets.Add(ticket);
        await _context.SaveChangesAsync();

        // BaseAuditableEntity.CreatedAt is stamped by the audit
        // interceptor as "now" on insert - backdate seed tickets
        // directly afterward so the demo data has a believable spread.
        ticket.CreatedAt = createdAt;
        await _context.SaveChangesAsync();
    }
}
