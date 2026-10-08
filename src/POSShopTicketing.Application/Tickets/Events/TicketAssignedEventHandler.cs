using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Events;

public class TicketAssignedEventHandler : INotificationHandler<TicketAssignedEvent>
{
    private readonly IAlertNotifier _alertNotifier;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;

    public TicketAssignedEventHandler(
        IAlertNotifier alertNotifier,
        IApplicationDbContext context,
        IEmailSender emailSender)
    {
        _alertNotifier = alertNotifier;
        _context = context;
        _emailSender = emailSender;
    }

    public async Task Handle(
        TicketAssignedEvent notification,
        CancellationToken cancellationToken)
    {
        // Create in-app notification
        await _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.TicketAssigned,
                $"Ticket {notification.TicketNumber} assigned to you",
                $"Ticket {notification.TicketNumber} was assigned to you.",
                notification.TenantId,
                notification.TicketId,
                notification.AssignedToTeamMemberId),
            cancellationToken);

        // Load assignee
        var assignee = await _context.TeamMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == notification.AssignedToTeamMemberId,
                cancellationToken);

        if (assignee is null)
        {
            return;
        }

        // Load ticket
        var ticket = await _context.Tickets
    .AsNoTracking()
    .FirstOrDefaultAsync(
        x => x.Id == notification.TicketId,
        cancellationToken);

        if (ticket is null)
        {
            return;
        }

        // Don't notify users about tickets assigned to themselves.
        if (ticket.CreatorId == notification.AssignedToTeamMemberId)
        {
            return;
        }

        // Send email
        await _emailSender.SendAsync(
            assignee.Email,
            assignee.FullName,
            $"Ticket {ticket.TicketNumber} Assigned",
            $@"
            <div style='font-family:Arial,sans-serif;font-size:14px'>
                <p>Hello {assignee.FullName},</p>

                <p>
                    A ticket has been assigned to you in RouteBoard.
                </p>

                <p>
                    <strong>Ticket Number:</strong>
                    {ticket.TicketNumber}
                </p>

                <p>
                    <strong>Subject:</strong>
                    {ticket.Subject}
                </p>

                <p>
                    Please log in to RouteBoard to review and process the ticket.
                </p>

                <p>
                    Thank you.
                </p>
            </div>",
            cancellationToken);
    }
}