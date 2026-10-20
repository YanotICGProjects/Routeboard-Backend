using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Events;

public sealed class TicketCreatedEventHandler
    : INotificationHandler<TicketCreatedEvent>
{
    private readonly IAlertNotifier _alertNotifier;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;
    private readonly ITicketEmailService _ticketEmailService;

    public TicketCreatedEventHandler(
        IAlertNotifier alertNotifier,
        IApplicationDbContext context,
        IEmailSender emailSender,
        ITicketEmailService ticketEmailService)
    {
        _alertNotifier = alertNotifier;
        _context = context;
        _emailSender = emailSender;
        _ticketEmailService = ticketEmailService;
    }

    public async Task Handle(
        TicketCreatedEvent notification,
        CancellationToken cancellationToken)
    {
        //
        // SYSTEM ALERT
        //
        await _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.Other,
                notification.IsUnverified
                    ? $"New Unverified Ticket {notification.TicketNumber}"
                    : $"New Ticket {notification.TicketNumber}",
                notification.Subject,
                notification.TenantId,
                notification.TicketId),
            cancellationToken);

        //
        // LOAD TICKET
        //
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
        // LOAD TENANT
        //
        var tenant = await _context.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == ticket.TenantId,
                cancellationToken);

        if (tenant is null)
        {
            return;
        }

        //
        // LOAD CREATOR
        //
        var creator = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.Id == ticket.CreatorId,
                cancellationToken);

        //
        // LOAD ASSIGNEE
        //
        TeamMember? assignee = null;

        if (ticket.AssignedToTeamMemberId.HasValue)
        {
            assignee = await _context.TeamMembers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.Id == ticket.AssignedToTeamMemberId.Value,
                    cancellationToken);
        }

        //
        // CREATOR NOTIFICATION
        //
        if (creator is not null)
        {
            _context.Notifications.Add(
                new Notification
                {
                    TenantId = ticket.TenantId,
                    TeamMemberId = creator.Id,
                    TicketId = ticket.Id,
                    Title = "Ticket Created",
                    Message =
                        $"Ticket {ticket.TicketNumber} has been created successfully.",
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow
                });
        }

        //
        // ASSIGNEE NOTIFICATION
        //
        if (assignee is not null
            && assignee.Id != ticket.CreatorId)
        {
            _context.Notifications.Add(
                new Notification
                {
                    TenantId = ticket.TenantId,
                    TeamMemberId = assignee.Id,
                    TicketId = ticket.Id,
                    Title = "Ticket Assigned",
                    Message =
                        $"Ticket {ticket.TicketNumber} has been assigned to you.",
                    IsRead = false,
                    CreatedAt = DateTime.UtcNow
                });
        }

        await _context.SaveChangesAsync(cancellationToken);

        //
        // CREATOR EMAIL
        //
        if (creator is not null
            && !string.IsNullOrWhiteSpace(creator.Email))
        {
            await _emailSender.SendAsync(
                creator.Email,
                creator.FullName,
                $"Ticket {ticket.TicketNumber} Created",
                $"""
                <div style="font-family:Arial,sans-serif;">
                    <h2>Ticket Created</h2>

                    <p>Hello {creator.FullName},</p>

                    <p>
                        Ticket <strong>{ticket.TicketNumber}</strong>
                        has been created successfully.
                    </p>

                    <p>
                        <strong>Subject:</strong>
                        {ticket.Subject}
                    </p>

                    <p>
                        Current Status:
                        <strong>{ticket.Status}</strong>
                    </p>
                </div>
                """,
                cancellationToken);
        }

        //
        // ASSIGNEE EMAIL
        //
        if (assignee is not null
            && !string.IsNullOrWhiteSpace(assignee.Email)
            && assignee.Id != ticket.CreatorId)
        {
            await _emailSender.SendAsync(
                assignee.Email,
                assignee.FullName,
                $"Ticket {ticket.TicketNumber} Assigned",
                $"""
                <div style="font-family:Arial,sans-serif;">
                    <h2>New Ticket Assignment</h2>

                    <p>Hello {assignee.FullName},</p>

                    <p>
                        Ticket
                        <strong>{ticket.TicketNumber}</strong>
                        has been assigned to you.
                    </p>

                    <p>
                        <strong>Subject:</strong>
                        {ticket.Subject}
                    </p>
                </div>
                """,
                cancellationToken);
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

            if (contact is not null &&
                !string.IsNullOrWhiteSpace(contact.Email))
            {
                var customerHtml =
                    $"""
                    <div style="background:#f4f7fa;padding:20px;font-family:Arial,sans-serif;">
                    
                        <div style="max-width:650px;margin:auto;background:#ffffff;border-radius:12px;padding:30px;">

                            <h2 style="color:#009c9c;margin-bottom:10px;">
                                {tenant.WorkSpaceName}
                            </h2>

                            <p>Hello {contact.FullName},</p>

                            <p>
                                We have received your request and created a support ticket.
                            </p>

                            <div style="background:#f8fafc;padding:15px;border-left:4px solid #009c9c;margin:20px 0;">
                                
                                <p>
                                    <strong>Ticket Number:</strong>
                                    {ticket.TicketNumber}
                                </p>

                                <p>
                                    <strong>Subject:</strong>
                                    {ticket.Subject}
                                </p>

                            </div>

                            <div>
                                {notification.CustomerEmailBody}
                            </div>

                            <br/>

                            <p>
                                Our support team will respond shortly.
                            </p>

                            <p>
                                Regards,
                                <br />
                                {tenant.WorkSpaceName}
                            </p>

                        </div>

                    </div>
                    """;

                await _ticketEmailService.SendAsync(
                    new TicketEmailRequest(
                        TicketNumber: ticket.TicketNumber,
                        Kind: TicketEmailKind.Conversation,
                        ToEmail: contact.Email,
                        ToName: contact.FullName,
                        Subject: $"[{ticket.TicketNumber}] {ticket.Subject}",
                        Html: customerHtml,
                        FromEmail: tenant.SupportEmail,
                        FromName: tenant.WorkSpaceName,
                        MailboxId: ticket.MailboxId),
                    cancellationToken);

                //
                // CUSTOMER EMAIL NOTIFICATION
                //
                _context.Notifications.Add(
                    new Notification
                    {
                        TenantId = ticket.TenantId,
                        TeamMemberId = ticket.CreatorId,
                        TicketId = ticket.Id,
                        Title = "Customer Email Sent",
                        Message =
                            $"Email sent to {contact.Email} for ticket {ticket.TicketNumber}.",
                        IsRead = false,
                        CreatedAt = DateTime.UtcNow
                    });

                if (ticket.AssignedToTeamMemberId.HasValue &&
                    ticket.AssignedToTeamMemberId.Value != ticket.CreatorId)
                {
                    _context.Notifications.Add(
                        new Notification
                        {
                            TenantId = ticket.TenantId,
                            TeamMemberId = ticket.AssignedToTeamMemberId.Value,
                            TicketId = ticket.Id,
                            Title = "Customer Email Sent",
                            Message =
                                $"Customer email sent for ticket {ticket.TicketNumber}.",
                            IsRead = false,
                            CreatedAt = DateTime.UtcNow
                        });
                }

                await _context.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
