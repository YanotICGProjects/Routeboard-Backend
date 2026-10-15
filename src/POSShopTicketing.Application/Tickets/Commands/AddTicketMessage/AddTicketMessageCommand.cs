using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Common;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Domain.Exceptions;

namespace POSShopTicketing.Application.Tickets.Commands.AddTicketMessage;

/// <summary>
/// POST /tickets/{id}/messages - an agent's reply or internal note.
/// A reply (Direction=Outbound) stamps FirstResponseAt the first time it
/// happens, and is enqueued through IBackgroundJobScheduler so the customer
/// actually receives it. An internal note never leaves POSShopTicketing.
///
/// REPLACES Application/Tickets/Commands/AddTicketMessage/AddTicketMessageCommand.cs
/// (the whole file - the ISendReplyEmailJob interface is at the bottom, unchanged).
///
/// WHAT CHANGED, and why (each point is marked "FIX n" in the code):
///   FIX 1  Message-IDs are stored WITHOUT angle brackets, lowercased - the same form the inbound
///          handler uses. Before, the reply's id was saved as "<guid@...>" while the inbound handler
///          looks for "guid@..." - so a customer's In-Reply-To never matched our message.
///   FIX 2  The reply's Message-ID now has a real domain (your mail domain) instead of
///          "{tenantId}.posshopticketing", which isn't a valid hostname and can hurt spam scoring.
///   FIX 3  The email job is enqueued AFTER the message is saved. Before, it was queued first, so a
///          fast worker could look for the message before it existed and silently send nothing.
///   FIX 4  If the ticket has no registered contact, the reply goes to the original sender's address
///          instead of silently not being emailed at all. Tickets still Unverified are refused with a
///          clear error (we don't email unrecognized senders until a manager has triaged them).
/// </summary>
public record AddTicketMessageCommand : IRequest<Guid>
{
    public Guid TicketId { get; init; }
    public MessageDirection Direction { get; init; } = MessageDirection.Outbound;
    public string Body { get; init; } = string.Empty;
}

public class AddTicketMessageCommandHandler : IRequestHandler<AddTicketMessageCommand, Guid>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IHtmlSanitizerService _htmlSanitizer;
    private readonly IBackgroundJobScheduler _backgroundJobScheduler;
    private readonly IDateTime _dateTime;
    private readonly ISystemMailboxAddressProvider _systemMailbox;

    public AddTicketMessageCommandHandler(
        IApplicationDbContext context,
        ICurrentUserService currentUserService,
        IHtmlSanitizerService htmlSanitizer,
        IBackgroundJobScheduler backgroundJobScheduler,
        IDateTime dateTime,
        ISystemMailboxAddressProvider systemMailbox)
    {
        _context = context;
        _currentUserService = currentUserService;
        _htmlSanitizer = htmlSanitizer;
        _backgroundJobScheduler = backgroundJobScheduler;
        _dateTime = dateTime;
        _systemMailbox = systemMailbox;
    }

    public async Task<Guid> Handle(AddTicketMessageCommand request, CancellationToken cancellationToken)
    {
        var ticket = await _context.Tickets
            .Include(t => t.OrganizationContact)
            .FirstOrDefaultAsync(t => t.Id == request.TicketId, cancellationToken)
            ?? throw new NotFoundException(nameof(Ticket), request.TicketId);

        var isReply = request.Direction == MessageDirection.Outbound;

        // FIX 4: decide WHO gets the email before changing anything, so a ticket that can't be
        // emailed fails loudly instead of saving a "reply" the customer never receives.
        (string Email, string? Name)? recipient = null;
        if (isReply)
        {
            recipient = ResolveRecipient(ticket)
                ?? throw new DomainException(
                    "This ticket's sender hasn't been verified yet, so a reply can't be emailed. " +
                    "Verify or assign the sender first, or add an internal note instead.");
        }

        // The last message the CUSTOMER sent: our reply points at it with In-Reply-To.
        // FIX 1: normalized, so old rows saved with "<...>" still compare correctly.
        string? lastInboundMessageId = null;
        if (isReply)
        {
            var stored = await _context.TicketMessages
                .Where(m => m.TicketId == ticket.Id && m.Direction == MessageDirection.Inbound)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.MessageId)
                .FirstOrDefaultAsync(cancellationToken);

            lastInboundMessageId = string.IsNullOrWhiteSpace(stored) ? null : MessageIdHelper.Normalize(stored);
        }

        // FIX 1 + 2: our own Message-ID for this reply, "guid@your-mail-domain", no brackets, lowercase.
        // (Same form the inbound handler stores and looks up.)
        string? generatedMessageId = isReply
            ? MessageIdHelper.Normalize($"{Guid.NewGuid():N}@{MailDomain()}")
            : null;

        var message = new TicketMessage
        {
            TenantId = ticket.TenantId,
            TicketId = ticket.Id,
            Direction = request.Direction,
            AuthorType = MessageAuthorType.TeamMember,
            AuthorTeamMemberId = _currentUserService.TeamMemberId,
            Body = _htmlSanitizer.Sanitize(request.Body),
            MessageId = generatedMessageId,
            InReplyToMessageId = lastInboundMessageId
        };

        _context.TicketMessages.Add(message);

        if (isReply && ticket.FirstResponseAt is null)
        {
            ticket.FirstResponseAt = _dateTime.Now;
        }

        await _context.SaveChangesAsync(cancellationToken);

        // FIX 3: only now - the message exists in the database - hand the email to Hangfire.
        if (isReply)
        {
            var subjectWithToken = SubjectTokenHelper.AppendToken($"Re: {ticket.Subject}", ticket.TicketNumber);

            // What we STORE has no brackets, but email headers are written with them ("<id>").
            // The senders accept either; passing the bracketed form is the standard-correct one.
            var headerMessageId = $"<{generatedMessageId}>";
            var headerInReplyTo = lastInboundMessageId is null ? null : $"<{lastInboundMessageId}>";

            _backgroundJobScheduler.Enqueue<ISendReplyEmailJob>(job => job.SendAsync(
                message.Id, recipient!.Value.Email, recipient.Value.Name,
                subjectWithToken, message.Body, headerInReplyTo, headerMessageId, CancellationToken.None));
        }

        return message.Id;
    }

    /// <summary>
    /// Registered contact first. Otherwise the address the ticket came from - but NOT for tickets still
    /// Unverified (unknown senders are quarantined; replying to them is a deliberate step after triage).
    /// Manually created tickets without a contact and without a sender return null.
    /// </summary>
    private static (string Email, string? Name)? ResolveRecipient(Ticket ticket)
    {
        if (ticket.OrganizationContact is not null)
        {
            return (ticket.OrganizationContact.Email, ticket.OrganizationContact.FirstName);
        }

        if (ticket.Status != TicketStatus.Unverified && !string.IsNullOrWhiteSpace(ticket.RawSenderEmail))
        {
            return (ticket.RawSenderEmail, null);
        }

        return null;
    }

    /// <summary>The domain of your system mailboxes, e.g. "support.routeboard.online".</summary>
    private string MailDomain()
    {
        var address = _systemMailbox.BuildAddress("msg");
        return address[(address.IndexOf('@') + 1)..];
    }
}

/// <summary>Hangfire-invokable job interface - kept in Application so
/// the enqueue call above doesn't reference Infrastructure directly;
/// implemented in Infrastructure/BackgroundJobs.</summary>
public interface ISendReplyEmailJob
{
    Task SendAsync(
        Guid ticketMessageId, string toEmail, string? toName, string subject, string bodyHtml,
        string? inReplyToMessageId, string generatedMessageId, CancellationToken cancellationToken);
}