using POSShopTicketing.Domain.Common;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Domain.Entities;

/// <summary>In-app notification feed for one TeamMember - populated by
/// the same domain-event handlers that drive IAlertNotifier
/// (assignment, escalation, SLA breach).</summary>
public class Notification : BaseEntity, ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid TeamMemberId { get; set; }
    public TeamMember? TeamMember { get; set; }

    public NotificationType Type { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    public Guid? TicketId { get; set; }

    public bool IsRead { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ReadAt { get; set; }
}
