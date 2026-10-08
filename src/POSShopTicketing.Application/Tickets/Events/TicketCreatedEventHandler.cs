using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Events;

public class TicketCreatedEventHandler : INotificationHandler<TicketCreatedEvent>
{
    private readonly IAlertNotifier _alertNotifier;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;

    public TicketCreatedEventHandler(
        IAlertNotifier alertNotifier,
        IApplicationDbContext context,
        IEmailSender emailSender)
    {
        _alertNotifier = alertNotifier;
        _context = context;
        _emailSender = emailSender;
    }

    public async Task Handle(
        TicketCreatedEvent notification,
        CancellationToken cancellationToken)
    {
        // Existing RouteBoard notification
        await _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.Other,
                notification.IsUnverified
                    ? $"New Unverified ticket {notification.TicketNumber}"
                    : $"New ticket {notification.TicketNumber}",
                notification.Subject,
                notification.TenantId,
                notification.TicketId),
            cancellationToken);

        var ticket = await _context.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == notification.TicketId,
                cancellationToken);

        if (ticket is null)
        {
            return;
        }

        // Email only for unassigned tickets
        if (ticket.AssignedToTeamMemberId.HasValue)
        {
            return;
        }

        var creator = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == ticket.CreatorId,
                cancellationToken);

        if (creator is null || string.IsNullOrWhiteSpace(creator.Email))
        {
            return;
        }

        await _emailSender.SendAsync(
            creator.Email,
            creator.FullName,
            $"Ticket {ticket.TicketNumber} Created",
            $@"
            <div style='font-family:Arial,sans-serif;font-size:14px;line-height:1.6'>
                <p>Hello {creator.FullName},</p>

                <p>
                    Your ticket has been created successfully in RouteBoard.
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
                    This ticket is currently awaiting assignment.
                </p>

                <p>
                    You will receive further updates when a team member is assigned.
                </p>

                <p>
                    Thank you.
                </p>
            </div>",
            cancellationToken);
    }
}