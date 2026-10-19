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

        //
        // INTERNAL EMAIL TO CREATOR
        //
        if (!ticket.AssignedToTeamMemberId.HasValue)
        {
            var creator = await _context.TeamMembers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == ticket.CreatorId,
                    cancellationToken);

            if (creator is not null &&
                !string.IsNullOrWhiteSpace(creator.Email))
            {
                await _emailSender.SendAsync(
                    creator.Email,
                    creator.FullName,
                    $"Ticket {ticket.TicketNumber} Created",
                    $"""
                <div style="font-family:Arial,sans-serif;font-size:14px;">
                    <p>Hello {creator.FullName},</p>

                    <p>
                        Your ticket has been created successfully.
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
                        This ticket is awaiting assignment.
                    </p>
                </div>
                """,
                    cancellationToken);
            }
        }

        //
        // CUSTOMER EMAIL
        //
        if (!string.IsNullOrWhiteSpace(notification.CustomerEmailBody))
        {
            var contact = await _context.OrganizationContacts
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == ticket.OrganizationContactId,
                    cancellationToken);

            if (contact is null ||
                string.IsNullOrWhiteSpace(contact.Email))
            {
                return;
            }

            await _emailSender.SendAsync(
                contact.Email,
                contact.FullName,
                $"[{ticket.TicketNumber}] {ticket.Subject}",
                notification.CustomerEmailBody,
                cancellationToken);
        }
    }
}