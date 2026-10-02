using System.Reflection;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Common;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Infrastructure.Persistence.Interceptors;

namespace POSShopTicketing.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    private readonly AuditableEntitySaveChangesInterceptor _auditInterceptor;
    private readonly TenantSessionInterceptor _tenantSessionInterceptor;
    private readonly ICurrentTenantService _currentTenantService;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        AuditableEntitySaveChangesInterceptor auditInterceptor,
        TenantSessionInterceptor tenantSessionInterceptor,
        ICurrentTenantService currentTenantService) : base(options)
    {
        _auditInterceptor = auditInterceptor;
        _tenantSessionInterceptor = tenantSessionInterceptor;
        _currentTenantService = currentTenantService;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<RefreshTokenz> RefreshTokens => Set<RefreshTokenz>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationDepartment> OrganizationDepartments => Set<OrganizationDepartment>();
    public DbSet<OrganizationContact> OrganizationContacts => Set<OrganizationContact>();
    public DbSet<AssignmentRule> AssignmentRules => Set<AssignmentRule>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<TicketStatusHistory> TicketStatusHistories => Set<TicketStatusHistory>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TicketTag> TicketTags => Set<TicketTag>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<EmailVerificationOtp> EmailVerificationOtps => Set<EmailVerificationOtp>();

    /// <summary>Guid.Empty sentinel for "no tenant in context" (see
    /// TeamMember.TenantId and Domain.Common.ITenantScoped) - referenced
    /// from the HasQueryFilter lambdas below, which EF Core evaluates
    /// per-query via this instance, so the filter always reflects the
    /// current request's tenant.</summary>
    private Guid CurrentTenantIdOrEmpty => _currentTenantService.TenantId ?? Guid.Empty;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(_auditInterceptor, _tenantSessionInterceptor);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        // One global sequence backs every tenant's TicketNumber (see
        // ITicketNumberGenerator) - simpler than a sequence per tenant,
        // and still fine since tenants also get distinct TicketPrefix
        // values, so numbers never look ambiguous even though the
        // numeric part alone is shared across tenants.
        builder.HasSequence<long>("TicketNumberSequence").StartsAt(1).IncrementsBy(1);

        // Isolation, layer 1: every ITenantScoped entity automatically
        // gets a global query filter scoping it to the caller's tenant -
        // see TenantSessionInterceptor for layer 2 (Postgres RLS).
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var method = typeof(ApplicationDbContext)
                .GetMethod(nameof(ApplyTenantQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType);

            method.Invoke(this, new object[] { builder });
        }

        base.OnModelCreating(builder);
    }

    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantScoped
    {
        builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == CurrentTenantIdOrEmpty);
    }
}
