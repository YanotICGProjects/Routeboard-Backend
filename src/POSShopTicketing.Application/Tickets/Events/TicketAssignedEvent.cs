using MediatR;

namespace POSShopTicketing.Application.Tickets.Events;

public record TicketAssignedEvent(
    Guid TenantId,
    Guid TicketId,
    string TicketNumber,
    Guid AssignedToTeamMemberId,
    Guid? AssignedByTeamMemberId = null
) : INotification;