using System.Net;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Events;

/// <summary>
/// REPLACES Application/Tickets/Events/TicketCreatedEventHandler.cs (the whole file).
///
/// Runs every time a ticket is created (by an agent in the app, or by a customer's email). It does three things,
/// and none of them can break the request that created the ticket:
///
///   1. Team: in-app notification + email to the ticket's CREATOR only (nothing if they assigned it to themselves).
///      The assignee is told by TicketAssignedEventHandler ("assigned"), never "created".
///   2. Customer, ticket raised by the CUSTOMER'S EMAIL (Source = Email, known contact):
///        an automatic no-reply "we received your email" notice. (Unknown senders get nothing: automatic replies to
///        strangers can be abused to send mail to people who never wrote to you.)
///   3. Customer, ticket CREATED BY AN AGENT (Source = Manual, contact with an email):
///        - agent wrote a message for the customer -> a CONVERSATION email from the workspace support address. This
///          STARTS the ticket's mail thread (the threadId is stored on the ticket). The message the agent typed was
///          already saved when the ticket was created, so we send THAT message instead of saving a second copy.
///        - agent wrote nothing for the customer   -> an automatic no-reply "ticket created" notice. The ticket has NO
///          threadId yet; it gets one when an agent first emails the customer.
///
/// "Customer Email Sent" in-app notifications for the team are added by TicketEmailService itself.
/// </summary>
public sealed class TicketCreatedEventHandler : INotificationHandler<TicketCreatedEvent>
{
    private readonly IAlertNotifier _alertNotifier;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;
    private readonly ITicketEmailService _ticketEmailService;
    private readonly INotificationEmailService _notificationEmailService;
    private readonly IHtmlSanitizerService _htmlSanitizer;
    private readonly ILogger<TicketCreatedEventHandler> _logger;

    public TicketCreatedEventHandler(
        IAlertNotifier alertNotifier,
        IApplicationDbContext context,
        IEmailSender emailSender,
        ITicketEmailService ticketEmailService,
        INotificationEmailService notificationEmailService,
        IHtmlSanitizerService htmlSanitizer,
        ILogger<TicketCreatedEventHandler> logger)
    {
        _alertNotifier = alertNotifier;
        _context = context;
        _emailSender = emailSender;
        _ticketEmailService = ticketEmailService;
        _notificationEmailService = notificationEmailService;
        _htmlSanitizer = htmlSanitizer;
        _logger = logger;
    }

    public async Task Handle(TicketCreatedEvent notification, CancellationToken cancellationToken)
    {
        // ---- System alert ----------------------------------------------------------------------------
        await SafeAsync("system alert", () => _alertNotifier.NotifyAsync(
            new AlertMessage(
                NotificationType.Other,
                notification.IsUnverified
                    ? $"New Unverified Ticket {notification.TicketNumber}"
                    : $"New Ticket {notification.TicketNumber}",
                notification.Subject,
                notification.TenantId,
                notification.TicketId),
            cancellationToken));

        // ---- Load what we need -----------------------------------------------------------------------
        var ticket = await _context.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == notification.TicketId, cancellationToken);

        if (ticket is null) return;

        var tenant = await _context.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ticket.TenantId, cancellationToken);

        if (tenant is null) return;

        var creator = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ticket.CreatorId, cancellationToken);

        // ---- 1. TEAM: only the CREATOR is told "ticket created" ---------------------------------------
        //  - The ASSIGNEE is never told "ticket created" here. They get the "ticket assigned" notice from
        //    TicketAssignedEventHandler, and nothing else (so they are not notified twice).
        //  - If the creator assigned the ticket to THEMSELVES, they already know: no notice at all.
        //  - The creator gets exactly ONE in-app notice and ONE email. Delete the in-app notice that
        //    CreateTicketCommand also adds, otherwise the creator sees it twice.
        var assignedToCreator = ticket.AssignedToTeamMemberId.HasValue
                                && ticket.AssignedToTeamMemberId.Value == ticket.CreatorId;
        var notifyCreator = creator is not null && !assignedToCreator;

        if (notifyCreator)
        {
            _context.Notifications.Add(NewNotification(
                ticket, creator!.Id, "Ticket Created",
                ticket.AssignedToTeamMemberId.HasValue
                    ? $"Ticket {ticket.TicketNumber} has been created successfully."
                    : $"Ticket {ticket.TicketNumber} was created and is awaiting assignment."));

            await SafeAsync("creator notification", () => _context.SaveChangesAsync(cancellationToken));

            if (!string.IsNullOrWhiteSpace(creator.Email))
            {
                await SafeAsync("creator email", () => _emailSender.SendAsync(
                    creator.Email, creator.FullName,
                    $"Ticket {ticket.TicketNumber} Created",
                    TeamEmail("Ticket Created", creator.FullName,
                        $"Ticket <strong>{Enc(ticket.TicketNumber)}</strong> has been created successfully.",
                        ticket),
                    cancellationToken));
            }
        }

        // ---- 2 + 3. CUSTOMER ---------------------------------------------------------------------------
        await SafeAsync("customer email", () => SendCustomerEmailAsync(ticket, tenant, notification, cancellationToken));
    }

    // =====================================================================================================
    //  CUSTOMER
    // =====================================================================================================
    private async Task SendCustomerEmailAsync(
        Ticket ticket, Tenant tenant, TicketCreatedEvent notification, CancellationToken ct)
    {
        if (!ticket.OrganizationContactId.HasValue) return;   // unknown sender or no contact: nobody to write to

        var contact = await _context.OrganizationContacts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == ticket.OrganizationContactId.Value, ct);

        if (contact is null || string.IsNullOrWhiteSpace(contact.Email)) return;

        // The customer's own email created this ticket -> "we received your email" (no-reply).
        if (ticket.Source == TicketSource.Email)
        {
            await _notificationEmailService.SendAutoReplyAsync(
                contact.Email, contact.FullName, ticket.TicketNumber, ticket.Subject, ct);
            return;
        }

        // An agent created the ticket and wrote nothing for the customer -> no-reply notice, NO thread yet.
        if (string.IsNullOrWhiteSpace(notification.CustomerEmailBody))
        {
            await _notificationEmailService.SendTicketCreatedAsync(
                contact.Email, contact.FullName, ticket.TicketNumber, ticket.Subject, ct);
            return;
        }

        // An agent created the ticket AND wrote a message for the customer -> a conversation email from the workspace
        // support address. It starts the thread and stores the threadId on the ticket.
        // The message was already saved with the ticket (Outbound, no Message-ID yet): send that very message.
        var initialMessageId = await _context.TicketMessages
            .AsNoTracking()
            .Where(m => m.TicketId == ticket.Id && m.Direction == MessageDirection.Outbound && m.MessageId == null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);

        try
        {
            await _ticketEmailService.SendAsync(new TicketEmailRequest(
                TicketNumber: ticket.TicketNumber,
                Kind: TicketEmailKind.Conversation,
                ToEmail: contact.Email,
                ToName: contact.FullName,
                Subject: null,                    // the service uses the ticket's own subject
                Html: CustomerHtml(tenant, contact, ticket, notification.CustomerEmailBody!),
                ExistingTicketMessageId: initialMessageId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The first email to the customer on ticket {Ticket} could not be sent", ticket.TicketNumber);

            // Let the person who created the ticket know, so it is not silently assumed to be sent.
            if (ticket.CreatorId != Guid.Empty)
            {
                _context.Notifications.Add(NewNotification(
                    ticket, ticket.CreatorId, "Customer Email Failed",
                    $"The email to {contact.Email} for ticket {ticket.TicketNumber} could not be sent. Open the ticket and send it again."));
                await _context.SaveChangesAsync(ct);
            }
        }
    }

    private string CustomerHtml(Tenant tenant, OrganizationContact contact, Ticket ticket, string agentBodyHtml)
    {
        var workspace = Enc(string.IsNullOrWhiteSpace(tenant.WorkSpaceName) ? tenant.Name : tenant.WorkSpaceName);

        // The agent's text is the only untrusted HTML here: sanitize it, then wrap it in our own template.
        var body = _htmlSanitizer.Sanitize(agentBodyHtml);

        return $"""
            <div style="background:#f4f7fa;padding:20px;font-family:Arial,sans-serif;">
                <div style="max-width:650px;margin:auto;background:#ffffff;border-radius:12px;padding:30px;">

                    <h2 style="color:#009c9c;margin-bottom:10px;">{workspace}</h2>

                    <p>Hello {Enc(contact.FullName)},</p>

                    <p>We have received your request and created a support ticket.</p>

                    <div style="background:#f8fafc;padding:15px;border-left:4px solid #009c9c;margin:20px 0;">
                        <p><strong>Ticket Number:</strong> {Enc(ticket.TicketNumber)}</p>
                        <p><strong>Subject:</strong> {Enc(ticket.Subject)}</p>
                    </div>

                    <div>{body}</div>

                    <br/>

                    <p>Our support team will respond shortly.</p>

                    <p>Regards,<br />{workspace}</p>
                </div>
            </div>
            """;
    }

    // =====================================================================================================
    //  HELPERS
    // =====================================================================================================
    private static Notification NewNotification(Ticket ticket, Guid teamMemberId, string title, string message) =>
        new()
        {
            TenantId = ticket.TenantId,
            TeamMemberId = teamMemberId,
            TicketId = ticket.Id,
            Title = title,
            Message = message,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

    private static string TeamEmail(string heading, string? name, string lineHtml, Ticket ticket) => $"""
        <div style="font-family:Arial,sans-serif;">
            <h2>{Enc(heading)}</h2>
            <p>Hello {Enc(name ?? string.Empty)},</p>
            <p>{lineHtml}</p>
            <p><strong>Subject:</strong> {Enc(ticket.Subject)}</p>
            <p>Current Status: <strong>{Enc(ticket.Status.ToString())}</strong></p>
        </div>
        """;

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Runs one side effect; a failure is logged and never stops the ticket creation that triggered it.</summary>
    private async Task SafeAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ticket created handler: {What} failed", what);
        }
    }
}