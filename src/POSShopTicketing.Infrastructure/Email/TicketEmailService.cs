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
using FluentValidation;

namespace POSShopTicketing.Infrastructure.Email;

/// <summary>
/// NEW FILE: Infrastructure/Email/TicketEmailService.cs
///
/// The ONE place that sends email to a customer about a ticket. The /api/email/send endpoint and the
/// automatic notices (NotificationEmailService) both go through it, so the rules from the call can't be
/// bypassed:
///
///   1. The ticket must already exist (looked up by its ticket number). This never creates a ticket.
///   2. The recipient must belong to that ticket: its customer (and, for automatic notices only, a team
///      member - e.g. "ticket assigned to you"). Nobody can use it to email strangers from your domain.
///   3. The SENDER is decided here from the database, never trusted from the request:
///        Notice        no-reply__{workspace}@support.routeboard.online
///        Conversation  the workspace mailbox the ticket belongs to
///   4. Conversation emails join the ticket's single mail thread (subject, In-Reply-To, References).
///
/// Runs inside a request that already has a tenant (a signed-in agent, or the inbound webhook after it has
/// resolved the workspace), so every query below is automatically limited to that tenant.
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

    public async Task<TicketEmailResult> SendAsync(TicketEmailRequest request, CancellationToken ct)
    {

        // ---- 1. Every email is tied to a ticket that ALREADY exists ----------------------------------
        var number = (request.TicketNumber ?? string.Empty).Trim().ToUpperInvariant();
        if (number.Length == 0)
        {
            throw new DomainException("Every email must be tied to a ticket - messageTicketNumber is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Html))
        {
            throw new DomainException("The email body (html) is required.");
        }

        var to = (request.ToEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (to.Length == 0 || !MailAddress.TryCreate(to, out _))
        {
            throw new DomainException("toEmail must be a valid email address.");
        }



        // Tenant-filtered, so a ticket number from another workspace simply isn't found.
        var ticket = await _context.Tickets
            .Include(t => t.OrganizationContact)
            .FirstOrDefaultAsync(t => t.TicketNumber == number, ct)
            ?? throw new NotFoundException(nameof(Ticket), number);

        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == ticket.TenantId, ct)
            ?? throw new NotFoundException(nameof(Tenant), ticket.TenantId);

        // ---- 2. The recipient must belong to this ticket ----------------------------------------------
        await EnsureRecipientBelongsToTicketAsync(ticket, to, request.Kind, ct);

        return request.Kind == TicketEmailKind.Notice
            ? await SendNoticeAsync(ticket, tenant, to, request, ct)
            : await SendConversationAsync(ticket, tenant, to, request, ct);
    }

    // =====================================================================================================
    //  NOTICE - automatic, no-reply, separate from the thread
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
            ReplyTo: from),                       // replies to a no-reply address are simply not read
            ct);

        return new TicketEmailResult(
            TicketEmailKind.Notice, ticket.Id, ticket.TicketNumber,
            MailboxId: null, MailboxAddress: null, TicketMessageId: null,
            ThreadId: null, MessageId: null, InReplyTo: null, References: null,
            From: from, Subject: subject, ResendId: resendId);
    }

    // =====================================================================================================
    //  CONVERSATION - an agent's message, from the workspace address, inside the ticket's mail thread
    // =====================================================================================================
    private async Task<TicketEmailResult> SendConversationAsync(
        Ticket ticket, Tenant tenant, string to, TicketEmailRequest request, CancellationToken ct)
    {
        // ---- Which workspace address does this come from? ---------------------------------------------
        var mailbox = await ResolveMailboxAsync(ticket, request.MailboxId, ct);
        EnsureFromMatches(request.FromEmail, mailbox.EmailAddress, "a conversation email");

        // ---- The thread: every message already in this ticket's conversation, oldest first ------------
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

        // In-Reply-To = the customer's latest message; if they have not written yet, our own latest one.
        var lastFromCustomer = existing
            .Where(m => m.Direction == MessageDirection.Inbound)
            .Select(m => MessageIdHelper.Normalize(m.MessageId))
            .LastOrDefault(id => id.Length > 0);

        var inReplyTo = !string.IsNullOrEmpty(lastFromCustomer) ? lastFromCustomer : ids.LastOrDefault();
        var references = BuildReferences(ids);

        // Subject: the ticket's title. "Re:" only once a conversation exists, so the first message an agent
        // sends on a ticket they raised by hand is simply titled with the ticket subject.
        // NO ticket tag in the subject: changing it would split the conversation in the customer's inbox.
        var subject = ids.Count > 0 ? ReplySubject(ticket.Subject) : ticket.Subject.Trim();

        // ---- Our own Message-ID for this email: what the customer's reply will point back at ---------
        var messageId = MessageIdHelper.Normalize($"{Guid.NewGuid():N}@{SystemDomain()}");

        // ---- Save the agent's message FIRST, so it is never lost -------------------------------------
        var message = new TicketMessage
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
        await _context.SaveChangesAsync(ct);

        // ---- Send ------------------------------------------------------------------------------------
        string? resendId;
        try
        {
            resendId = await _sender.SendAsync(new OutboundEmail(
                ToEmail: to,
                ToName: request.ToName,
                FromEmail: mailbox.EmailAddress,                  // the customer sees the workspace address...
                FromName: string.IsNullOrWhiteSpace(request.FromName) ? tenant.Name : request.FromName,
                Subject: subject,
                Html: message.Body,
                ReplyTo: mailbox.EmailAddress,                    // ...and their answer comes back to it
                MessageId: messageId,
                InReplyTo: inReplyTo,
                References: references),
                ct);
        }
        catch
        {
            // The email did not go out, so the conversation must not show it as sent.
            await RemoveUnsentMessageAsync(message);
            throw;
        }

        // ---- Only now that it is really sent: remember the mailbox and the first response ------------
        try
        {
            if (ticket.MailboxId is null) ticket.MailboxId = mailbox.Id;          // a manual ticket gets its mailbox here
            if (ticket.FirstResponseAt is null) ticket.FirstResponseAt = _dateTime.Now;
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The customer already has the email; log loudly rather than tell the agent it failed.
            _logger.LogError(ex, "Email for ticket {Ticket} was sent but the ticket could not be updated", ticket.TicketNumber);
        }

        return new TicketEmailResult(
            TicketEmailKind.Conversation, ticket.Id, ticket.TicketNumber,
            MailboxId: mailbox.Id, MailboxAddress: mailbox.EmailAddress,
            TicketMessageId: message.Id,
            ThreadId: ids.FirstOrDefault() ?? messageId,      // the thread is identified by its first message
            MessageId: messageId, InReplyTo: inReplyTo, References: references,
            From: mailbox.EmailAddress, Subject: subject, ResendId: resendId);
    }

    // =====================================================================================================
    //  Helpers
    // =====================================================================================================

    /// <summary>
    /// Conversation recipient = the ticket's customer. Notice recipient = the customer, or a team member of this
    /// workspace (for notices like "ticket assigned to you"). Anything else is refused.
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
    /// Picks the mailbox (the workspace address) the email is sent from. In order:
    ///   1. the mailbox already stored on the ticket (it was raised by email, so this is where it arrived)
    ///   2. the mailboxId in the request (a ticket created by hand has no mailbox yet)
    ///   3. the workspace's default verified mailbox
    ///   4. any verified system mailbox of the workspace
    /// The address must be on the platform's verified sending domain; a tenant's own custom-domain mailbox
    /// can't send directly, so its system mailbox is used instead.
    /// </summary>
    private async Task<Mailbox> ResolveMailboxAsync(Ticket ticket, Guid? requestedMailboxId, CancellationToken ct)
    {
        Mailbox? mailbox = null;

        if (ticket.MailboxId.HasValue)
        {
            mailbox = await _context.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == ticket.MailboxId.Value, ct);
        }

        if (mailbox is null && requestedMailboxId.HasValue)
        {
            mailbox = await _context.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == requestedMailboxId.Value && m.IsVerified, ct)
                ?? throw new DomainException("mailboxId does not belong to this workspace, or it is not verified yet.");
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
            "This workspace has no verified mailbox on the platform domain yet. Create one under Mailboxes first.");
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

    /// <summary>The whole chain, oldest first. Very long chains keep the FIRST id (the thread root) plus the newest ones.</summary>
    private static string? BuildReferences(List<string> ids)
    {
        if (ids.Count == 0) return null;

        var chain = ids.Count > MaxReferences
            ? ids.Take(1).Concat(ids.Skip(ids.Count - (MaxReferences - 1))).ToList()
            : ids;

        return string.Join(' ', chain);
    }

    /// <summary>"Printer jammed" -> "Re: Printer jammed"; a subject that already starts with Re: is left alone.</summary>
    private static string ReplySubject(string subject)
    {
        var trimmed = (subject ?? string.Empty).Trim();
        return trimmed.StartsWith("re:", StringComparison.OrdinalIgnoreCase) ? trimmed : $"Re: {trimmed}";
    }

    private async Task RemoveUnsentMessageAsync(TicketMessage message)
    {
        try
        {
            _context.TicketMessages.Remove(message);
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove message {MessageId} after the email failed to send", message.Id);
        }
    }
}