using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Common;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Domain.Exceptions;

namespace POSShopTicketing.Infrastructure.Email;

/// <summary>
/// REPLACES Infrastructure/Email/TicketEmailService.cs (the whole file).
///
/// The ONE place that sends email to a customer about a ticket. Rules:
///
///   1. The ticket must already exist (found by its ticket number). Nothing here creates a ticket.
///   2. The recipient must belong to the ticket: its customer (and, for automatic notices only, a team member).
///   3. The SENDER is decided here, never trusted from the request:
///        Notice        no-reply__{workspace}@support.routeboard.online
///        Conversation  the workspace's support mailbox
///   4. ONE TICKET = ONE MAIL THREAD, identified by Ticket.ThreadId:
///        - the first Conversation email on a ticket that has no thread STARTS it: its own Message-ID becomes the
///          threadId and is stored on the ticket
///        - every later Conversation email continues the SAME thread (same subject, In-Reply-To, and a References
///          chain that starts with the threadId)
///   5. When a Conversation email goes out, the team gets an in-app "Customer Email Sent" notification.
///
/// Runs inside a request that already has a tenant (a signed-in agent, or the inbound webhook after it has resolved
/// the workspace), so every query is automatically limited to that tenant.
/// </summary>
public class TicketEmailService : ITicketEmailService
{
    private const int MaxReferences = 12;   // keep the header a sane length on very long conversations

    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ISystemMailboxAddressProvider _systemMailbox;
    private readonly IHtmlSanitizerService _htmlSanitizer;
    private readonly IOutboundEmailSender _sender;
    private readonly IDateTime _dateTime;
    private readonly ILogger<TicketEmailService> _logger;

    public TicketEmailService(
        IApplicationDbContext context,
        ICurrentUserService currentUser,
        ISystemMailboxAddressProvider systemMailbox,
        IHtmlSanitizerService htmlSanitizer,
        IOutboundEmailSender sender,
        IDateTime dateTime,
        ILogger<TicketEmailService> logger)
    {
        _context = context;
        _currentUser = currentUser;
        _systemMailbox = systemMailbox;
        _htmlSanitizer = htmlSanitizer;
        _sender = sender;
        _dateTime = dateTime;
        _logger = logger;
    }

    // =====================================================================================================
    //  SEND
    // =====================================================================================================
    public async Task<TicketEmailResult> SendAsync(TicketEmailRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Html))
        {
            throw new DomainException("The email body (html) is required.");
        }

        var to = (request.ToEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (to.Length == 0 || !MailAddress.TryCreate(to, out _))
        {
            throw new DomainException("toEmail must be a valid email address.");
        }

        var (ticket, tenant) = await LoadTicketAsync(request.TicketNumber, ct);

        await EnsureRecipientBelongsToTicketAsync(ticket, to, request.Kind, ct);

        return request.Kind == TicketEmailKind.Notice
            ? await SendNoticeAsync(ticket, tenant, to, request, ct)
            : await SendConversationAsync(ticket, tenant, to, request, ct);
    }

    // =====================================================================================================
    //  NOTICE - automatic, no-reply, never part of the thread
    // =====================================================================================================
    private async Task<TicketEmailResult> SendNoticeAsync(
        Ticket ticket, Tenant tenant, string to, TicketEmailRequest request, CancellationToken ct)
    {
        var from = NoReplyAddress(tenant);

        // The caller may state the sender, but it must be exactly what we would have chosen (no spoofing).
        EnsureFromMatches(request.FromEmail, from, "an automatic notice");

        // A notice always says which ticket it is about, even if the caller forgot to put it in the subject.
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? $"[{ticket.TicketNumber}] Update on your ticket"
            : request.Subject.Contains(ticket.TicketNumber, StringComparison.OrdinalIgnoreCase)
                ? request.Subject.Trim()
                : $"[{ticket.TicketNumber}] {request.Subject.Trim()}";

        var resendId = await _sender.SendAsync(new OutboundEmail(
            ToEmail: to,
            ToName: request.ToName,
            FromEmail: from,
            FromName: string.IsNullOrWhiteSpace(request.FromName) ? "No Reply" : request.FromName,
            Subject: subject,
            Html: _htmlSanitizer.Sanitize(request.Html),
            ReplyTo: from),                       // replies to a no-reply address are not read
            ct);

        return new TicketEmailResult(
            TicketEmailKind.Notice, ticket.Id, ticket.TicketNumber,
            ThreadId: null, TicketMessageId: null, MessageId: null, InReplyTo: null, References: null,
            From: from, Subject: subject, ResendId: resendId);
    }

    // =====================================================================================================
    //  CONVERSATION - an agent's message, from the workspace support address, in the ticket's one thread
    // =====================================================================================================
    private async Task<TicketEmailResult> SendConversationAsync(
        Ticket ticket, Tenant tenant, string to, TicketEmailRequest request, CancellationToken ct)
    {
        // ---- threadId from the caller must match the ticket's own (empty = "start / continue the ticket's thread") ----
        var ticketThread = MessageIdHelper.Normalize(ticket.ThreadId);
        var requestedThread = MessageIdHelper.Normalize(request.ThreadId);

        if (requestedThread.Length > 0 && requestedThread != ticketThread)
        {
            throw new DomainException(ticketThread.Length == 0
                ? "This ticket has no mail thread yet. Leave threadId empty for the first message to the customer."
                : "threadId does not belong to this ticket.");
        }

        // ---- Sender: the workspace support mailbox ----------------------------------------------------
        var mailbox = await ResolveMailboxAsync(ticket, ct);
        EnsureFromMatches(request.FromEmail, mailbox.EmailAddress, "a conversation email");

        // ---- What is already in the thread (oldest first) ---------------------------------------------
        var existing = await _context.TicketMessages
            .AsNoTracking()
            .Where(m => m.TicketId == ticket.Id && m.MessageId != null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.MessageId, m.Direction })
            .ToListAsync(ct);

        var ids = existing
            .Select(m => MessageIdHelper.Normalize(m.MessageId))
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();

        var lastFromCustomer = existing
            .Where(m => m.Direction == MessageDirection.Inbound)
            .Select(m => MessageIdHelper.Normalize(m.MessageId))
            .LastOrDefault(id => id.Length > 0);

        // Our own Message-ID for this email: what the customer's reply will point back at.
        var messageId = MessageIdHelper.Normalize($"{Guid.NewGuid():N}@{SystemDomain()}");

        // The thread: the ticket's stored threadId; else the first message we know of; else THIS email starts it.
        var threadId = ticketThread.Length > 0 ? ticketThread : ids.FirstOrDefault() ?? string.Empty;
        var startsNewThread = threadId.Length == 0;
        if (startsNewThread) threadId = messageId;

        // References = the whole chain, always beginning with the threadId.
        var chain = new List<string>(ids);
        if (!startsNewThread && !chain.Contains(threadId)) chain.Insert(0, threadId);

        var references = BuildReferences(chain);
        var inReplyTo = !string.IsNullOrEmpty(lastFromCustomer) ? lastFromCustomer : chain.LastOrDefault();

        // Subject: the ticket's title. "Re:" only once a thread exists. NO ticket tag in it - changing the
        // subject would split the conversation in the customer's inbox.
        var subject = startsNewThread ? ticket.Subject.Trim() : ReplySubject(ticket.Subject);

        // ---- Save the agent's message FIRST, so it is never lost -------------------------------------
        TicketMessage message;
        var reusedExistingMessage = false;

        if (request.ExistingTicketMessageId.HasValue)
        {
            // Ticket creation already saved the agent's first message; we only give it its thread identity.
            message = await _context.TicketMessages.FirstOrDefaultAsync(
                    m => m.Id == request.ExistingTicketMessageId.Value
                         && m.TicketId == ticket.Id
                         && m.Direction == MessageDirection.Outbound
                         && m.MessageId == null, ct)
                ?? throw new DomainException("The message to send was not found on this ticket, or it was already sent.");

            message.MessageId = messageId;
            message.InReplyToMessageId = inReplyTo;
            reusedExistingMessage = true;
        }
        else
        {
            message = new TicketMessage
            {
                TenantId = ticket.TenantId,
                TicketId = ticket.Id,
                Direction = MessageDirection.Outbound,
                AuthorType = MessageAuthorType.TeamMember,
                AuthorTeamMemberId = _currentUser.TeamMemberId,
                Body = _htmlSanitizer.Sanitize(request.Html),
                MessageId = messageId,
                InReplyToMessageId = inReplyTo
            };

            _context.TicketMessages.Add(message);
        }

        await _context.SaveChangesAsync(ct);

        // ---- Send ------------------------------------------------------------------------------------
        string? resendId;
        try
        {
            resendId = await _sender.SendAsync(new OutboundEmail(
                ToEmail: to,
                ToName: request.ToName,
                FromEmail: mailbox.EmailAddress,                  // the customer sees the workspace support address...
                FromName: FirstNonEmpty(request.FromName, tenant.WorkSpaceName, tenant.Name),
                Subject: subject,
                // A caller that passes ExistingTicketMessageId is our own trusted code (it has already sanitized the
                // body and wrapped it in the email template); everything else is sanitized.
                Html: reusedExistingMessage ? request.Html : message.Body,
                ReplyTo: mailbox.EmailAddress,                    // ...and their answer comes back to it
                MessageId: messageId,
                InReplyTo: inReplyTo,
                References: references),
                ct);
        }
        catch
        {
            // The email did not go out, so the conversation must not show it as sent.
            await UndoUnsentMessageAsync(message, reusedExistingMessage);
            throw;
        }

        // ---- Only now that it is really sent: store the thread, the mailbox, the first response, notify the team ----
        try
        {
            if (string.IsNullOrEmpty(ticket.ThreadId)) ticket.ThreadId = threadId;   // the thread is born here for a manual ticket
            if (ticket.MailboxId is null) ticket.MailboxId = mailbox.Id;
            if (ticket.FirstResponseAt is null) ticket.FirstResponseAt = _dateTime.Now;

            AddTeamNotifications(ticket, to);

            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The customer already has the email; log loudly rather than tell the agent it failed.
            _logger.LogError(ex, "Email for ticket {Ticket} was sent but the ticket could not be updated", ticket.TicketNumber);
        }

        return new TicketEmailResult(
            TicketEmailKind.Conversation, ticket.Id, ticket.TicketNumber,
            ThreadId: threadId, TicketMessageId: message.Id,
            MessageId: messageId, InReplyTo: inReplyTo, References: references,
            From: mailbox.EmailAddress, Subject: subject, ResendId: resendId);
    }

    // =====================================================================================================
    //  THREAD INFO - what the UI needs before the agent writes to the customer
    // =====================================================================================================
    public async Task<TicketThreadInfo> GetThreadAsync(string ticketNumber, CancellationToken ct)
    {
        var (ticket, tenant) = await LoadTicketAsync(ticketNumber, ct);

        string? conversationFrom = null;
        try
        {
            conversationFrom = (await ResolveMailboxAsync(ticket, ct)).EmailAddress;
        }
        catch (DomainException)
        {
            // The workspace has no usable mailbox yet; the UI can still show the rest.
        }

        var thread = MessageIdHelper.Normalize(ticket.ThreadId);

        var hasMessagesWithIds = thread.Length > 0 || await _context.TicketMessages
            .AnyAsync(m => m.TicketId == ticket.Id && m.MessageId != null, ct);

        var customerEmail = !string.IsNullOrWhiteSpace(ticket.OrganizationContact?.Email)
            ? ticket.OrganizationContact!.Email
            : string.IsNullOrWhiteSpace(ticket.RawSenderEmail) ? null : ticket.RawSenderEmail;

        return new TicketThreadInfo(
            TicketId: ticket.Id,
            TicketNumber: ticket.TicketNumber,
            HasThread: hasMessagesWithIds,
            ThreadId: thread.Length > 0 ? thread : null,
            ConversationFrom: conversationFrom,
            NoReplyFrom: NoReplyAddress(tenant),
            NextSubject: hasMessagesWithIds ? ReplySubject(ticket.Subject) : ticket.Subject.Trim(),
            CustomerEmail: customerEmail,
            CustomerName: ticket.OrganizationContact?.FullName);
    }

    // =====================================================================================================
    //  Helpers
    // =====================================================================================================

    /// <summary>Every email is tied to a ticket that ALREADY exists. Tenant-filtered, so another workspace's number isn't found.</summary>
    private async Task<(Ticket Ticket, Tenant Tenant)> LoadTicketAsync(string? ticketNumber, CancellationToken ct)
    {
        var number = (ticketNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (number.Length == 0)
        {
            throw new DomainException("Every email must be tied to a ticket - messageTicketNumber is required.");
        }

        var ticket = await _context.Tickets
            .Include(t => t.OrganizationContact)
            .FirstOrDefaultAsync(t => t.TicketNumber == number, ct)
            ?? throw new NotFoundException(nameof(Ticket), number);

        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticket.TenantId, ct)
            ?? throw new NotFoundException(nameof(Tenant), ticket.TenantId);

        return (ticket, tenant);
    }

    /// <summary>
    /// Conversation recipient = the ticket's customer. Notice recipient = the customer, or a team member of this
    /// workspace (for notices like "ticket assigned to you" / "customer replied"). Anything else is refused.
    /// </summary>
    private async Task EnsureRecipientBelongsToTicketAsync(
        Ticket ticket, string to, TicketEmailKind kind, CancellationToken ct)
    {
        var customerEmails = new[] { ticket.OrganizationContact?.Email, ticket.RawSenderEmail }
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim().ToLowerInvariant());

        if (customerEmails.Contains(to)) return;

        if (kind == TicketEmailKind.Notice && await _context.TeamMembers.AnyAsync(m => m.Email == to, ct)) return;

        throw new DomainException(
            $"{to} is not the customer of ticket {ticket.TicketNumber}. " +
            "Emails can only be sent about a ticket to that ticket's own customer.");
    }

    /// <summary>
    /// The workspace support mailbox a Conversation email comes from. In order:
    ///   1. the mailbox the ticket already has (it was raised by email, so this is where it arrived)
    ///   2. the workspace's default verified mailbox
    ///   3. any verified system mailbox of the workspace
    /// It must be on the platform's verified sending domain; a tenant's own custom-domain mailbox can't send
    /// directly, so its system mailbox is used instead.
    /// </summary>
    private async Task<Mailbox> ResolveMailboxAsync(Ticket ticket, CancellationToken ct)
    {
        Mailbox? mailbox = null;

        if (ticket.MailboxId.HasValue)
        {
            mailbox = await _context.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == ticket.MailboxId.Value, ct);
        }

        mailbox ??= await _context.Mailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.IsVerified && m.IsDefault, ct);

        mailbox ??= await _context.Mailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.IsVerified && m.IsSystemProvided, ct);

        if (mailbox is not null && !_systemMailbox.IsSystemDomain(mailbox.EmailAddress))
        {
            mailbox = await _context.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.IsVerified && m.IsSystemProvided, ct);
        }

        return mailbox ?? throw new DomainException(
            "This workspace has no verified support mailbox on the platform domain yet. Create one under Mailboxes first.");
    }

    /// <summary>In-app "Customer Email Sent" notice for the ticket's creator and assignee (not for the agent who just sent it).</summary>
    private void AddTeamNotifications(Ticket ticket, string customerEmail)
    {
        var actor = _currentUser.TeamMemberId;

        var recipients = new List<Guid?> { ticket.CreatorId == Guid.Empty ? null : ticket.CreatorId, ticket.AssignedToTeamMemberId }
            .Where(id => id.HasValue && id != actor)
            .Select(id => id!.Value)
            .Distinct();

        foreach (var teamMemberId in recipients)
        {
            _context.Notifications.Add(new Notification
            {
                TenantId = ticket.TenantId,
                TeamMemberId = teamMemberId,
                TicketId = ticket.Id,
                Title = "Customer Email Sent",
                Message = $"An email was sent to {customerEmail} on ticket {ticket.TicketNumber}.",
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private static void EnsureFromMatches(string? requestedFrom, string expectedFrom, string what)
    {
        if (!string.IsNullOrWhiteSpace(requestedFrom) &&
            !string.Equals(requestedFrom.Trim(), expectedFrom, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"fromEmail for {what} must be {expectedFrom}.");
        }
    }

    /// <summary>no-reply__pos-shop@support.routeboard.online  (workspace alias between "no-reply__" and "@").</summary>
    private string NoReplyAddress(Tenant tenant)
    {
        var alias = !string.IsNullOrWhiteSpace(tenant.WorkspaceAlias) ? tenant.WorkspaceAlias : tenant.Slug;
        var clean = Regex.Replace((alias ?? string.Empty).Trim().ToLowerInvariant(), "[^a-z0-9.-]+", "-").Trim('-');
        if (clean.Length == 0) clean = "workspace";

        return $"no-reply__{clean}@{SystemDomain()}";
    }

    /// <summary>"support.routeboard.online" - the domain of your system mailboxes.</summary>
    private string SystemDomain()
    {
        var address = _systemMailbox.BuildAddress("x");
        return address[(address.IndexOf('@') + 1)..];
    }

    /// <summary>The whole chain, oldest first. Very long chains keep the FIRST id (the threadId) plus the newest ones.</summary>
    private static string? BuildReferences(List<string> chain)
    {
        if (chain.Count == 0) return null;

        var limited = chain.Count > MaxReferences
            ? chain.Take(1).Concat(chain.Skip(chain.Count - (MaxReferences - 1))).ToList()
            : chain;

        return string.Join(' ', limited);
    }

    /// <summary>"Printer jammed" -> "Re: Printer jammed"; a subject that already starts with Re: is left alone.</summary>
    private static string ReplySubject(string subject)
    {
        var trimmed = (subject ?? string.Empty).Trim();
        return trimmed.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Re: {trimmed}";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>The send failed: a new message is removed; a message that already existed just loses its thread identity.</summary>
    private async Task UndoUnsentMessageAsync(TicketMessage message, bool existedBefore)
    {
        try
        {
            if (existedBefore)
            {
                message.MessageId = null;
                message.InReplyToMessageId = null;
            }
            else
            {
                _context.TicketMessages.Remove(message);
            }

            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not undo message {MessageId} after the email failed to send", message.Id);
        }
    }
}