using POSShopTicketing.Domain.Common;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Domain.Entities;

/// <summary>
/// The "Service Provider" from the spec - the company running support.
/// Owns every tenant-scoped row below it. Not itself tenant-scoped (it
/// IS the tenant), so it carries no TenantId and no global query filter -
/// only Platform SuperAdmin-authorized endpoints touch this table.
/// </summary>
public class Tenant : BaseAuditableEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Used to build the default inbound address
    /// ({slug}@support.routeboard-style domain) and as a human-readable
    /// handle - must be URL/subdomain safe.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Short uppercase code used as the TicketNumber prefix,
    /// e.g. "SP123" -> tickets like SP123-000456. Defaults from Slug at
    /// creation but can be changed independently.</summary>
    public string TicketPrefix { get; set; } = string.Empty;

    public TenantPlan Plan { get; set; } = TenantPlan.Trial;

    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public string WorkspaceAlias { get; set; } = string.Empty;
    public string WorkspaceDomain { get; set; } = string.Empty;
    public string SupportEmail { get; set; } = string.Empty;

    public ICollection<TeamMember> TeamMembers { get; set; } = new List<TeamMember>();
    public ICollection<Organization> Organizations { get; set; } = new List<Organization>();
    public ICollection<Mailbox> Mailboxes { get; set; } = new List<Mailbox>();
}
