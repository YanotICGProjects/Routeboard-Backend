using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Events;

public class TicketAssignedEventHandler
    : INotificationHandler<TicketAssignedEvent>
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
        var ticket = await _context.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == notification.TicketId,
                cancellationToken);

        if (ticket is null)
        {
            return;
        }

        var assignee = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == notification.AssignedToTeamMemberId,
                cancellationToken);

        if (assignee is null)
        {
            return;
        }

        var assigner = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == notification.AssignedByTeamMemberId,
                cancellationToken);

        if (assigner is null)
        {
            return;
        }

        // Self-assignment
        if (assigner.Id == assignee.Id)
        {
            return;
        }

        // =====================================================
        // ASSIGNEE NOTIFICATION + EMAIL
        // =====================================================

        await _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.TicketAssigned,
                $"Ticket {ticket.TicketNumber} assigned to you",
                $"Ticket {ticket.TicketNumber} was assigned to you.",
                notification.TenantId,
                ticket.Id,
                assignee.Id),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(assignee.Email))
        {
            var assigneeHtmlBody = $@"
            <div style='font-family:Arial,sans-serif;font-size:14px;line-height:1.6'>
                <p>Hello {assignee.FullName},</p>

                <p>
                    A ticket has been assigned to you in RouteBoard.
                </p>

                <p>
                    <strong>Ticket Number:</strong><br/>
                    {ticket.TicketNumber}
                </p>

                <p>
                    <strong>Subject:</strong><br/>
                    {ticket.Subject}
                </p>

                <p>
                    Please log in to RouteBoard to review and process this ticket.
                </p>

                <p>
                    Thank you.
                </p>
            </div>";

            await _emailSender.SendAsync(
                assignee.Email,
                assignee.FullName,
                $"Ticket {ticket.TicketNumber} Assigned",
                assigneeHtmlBody,
                cancellationToken);
        }

        // =====================================================
        // ASSIGNER NOTIFICATION + EMAIL
        // =====================================================

        await _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.TicketAssigned,
                $"Ticket {ticket.TicketNumber} assigned",
                $"You assigned Ticket {ticket.TicketNumber} to {assignee.FullName}.",
                notification.TenantId,
                ticket.Id,
                assigner.Id),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(assigner.Email))
        {
            var assignerHtmlBody = $@"
            <div style='font-family:Arial,sans-serif;font-size:14px;line-height:1.6'>
                <p>Hello {assigner.FullName},</p>

                <p>
                    You successfully assigned a ticket in RouteBoard.
                </p>

                <p>
                    <strong>Ticket Number:</strong><br/>
                    {ticket.TicketNumber}
                </p>

                <p>
                    <strong>Subject:</strong><br/>
                    {ticket.Subject}
                </p>

                <p>
                    <strong>Assigned To:</strong><br/>
                    {assignee.FullName}
                </p>

                <p>
                    Thank you.
                </p>
            </div>";

            await _emailSender.SendAsync(
                assigner.Email,
                assigner.FullName,
                $"Ticket {ticket.TicketNumber} Assigned",
                assignerHtmlBody,
                cancellationToken);
        }
    }
}