using POSShopTicketing.Domain.Common;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Domain.Entities;

/// <summary>The work item. Optionally linked to an Organization, an
/// OrganizationTeam, and an OrganizationMember - "optionally" because an
/// Unverified ticket from an unrecognized sender starts out linked to
/// none of them (see the triage flow).</summary>
public class Ticket : BaseAuditableEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    /// <summary>Human-facing reference shown in the UI and email subject
    /// lines, e.g. "SP123-000456" - never the internal UUID.</summary>
    public string TicketNumber { get; set; } = string.Empty;

    public Guid? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public Guid? OrganizationDepartmentId { get; set; }

    public string? DepartmentName { get; set; }
    public OrganizationDepartment? OrganizationDepartment { get; set; }

    public Guid? OrganizationContactId { get; set; }

    public string? ContactFirstName { get; set; }
    public string? ContactLastName { get; set; }
    public OrganizationContact? OrganizationContact { get; set; }

    public Guid CreatorId { get; set; }
    public string? CreatorName { get; set; }

    /// <summary>Captured regardless of whether the sender matched a
    /// known OrganizationMember, for triage and audit.</summary>
    public string RawSenderEmail { get; set; } = string.Empty;

    /// <summary>Nullable deviation from the spec's schema (which lists
    /// mailbox_id as not-null): a Manual or Api-sourced ticket has no
    /// inbound mailbox to attribute, so this is only required for
    /// Source = Email.</summary>
    public Guid? MailboxId { get; set; }
    public Mailbox? Mailbox { get; set; }

    public string Subject { get; set; } = string.Empty;

    public TicketStatus Status { get; set; } = TicketStatus.New;

    public TicketPriority Priority { get; set; } = TicketPriority.Medium;

    public TicketSource Source { get; set; } = TicketSource.Manual;

    public Guid? AssignedToTeamMemberId { get; set; }
    public TeamMember? AssignedToTeamMember { get; set; }

    /// <summary>Manually set for now; SLA-driven automatically once
    /// Phase 5's business-hours engine reads DueAt.</summary>
    public bool Escalated { get; set; }
    public bool isSendToCustomer { get; set; }
    public DateTime? FirstResponseAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string TransportationRoute { get; set; } = string.Empty;
    public string TransportationAmount { get; set; } = string.Empty;

    /// <summary>Populated once SLA policies apply (see SlaPolicy) - null
    /// until then.</summary>
    public DateTime? DueAt { get; set; }

    public ICollection<TicketMessage> Messages { get; set; } = new List<TicketMessage>();
    public ICollection<TicketStatusHistory> StatusHistory { get; set; } = new List<TicketStatusHistory>();
    public ICollection<TicketTag> Tags { get; set; } = new List<TicketTag>();
}
