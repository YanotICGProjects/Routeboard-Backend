using System.Net;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Common;
using POSShopTicketing.Application.Tickets.Events;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Commands.IngestInboundEmail;

/// <summary>
/// REPLACES Application/Tickets/Commands/IngestInboundEmail/IngestInboundEmailCommand.cs (the whole file).
/// (InboundEmailOutcome and IngestInboundEmailResult stay where they already are in your solution.)
///
/// THE INBOUND PIPELINE - now decided by the THREAD ID:
///   1. Ignore robots (auto-replies / bounces).
///   2. Work out the workspace from the ENVELOPE RECIPIENT only.
///   3. De-duplicate by Message-ID (providers retry webhooks).
///   4. Work out the email's threadId: the FIRST message-id of its References header (the root of the thread), else its
///      In-Reply-To, else its own Message-ID (a brand-new email starts a brand-new thread).
///        - threadId already belongs to a ticket -> it is a REPLY: add the email to that ticket's conversation.
///          NO new ticket. (A closed ticket is reopened.)
///        - threadId is new                      -> raise a NEW ticket and store the threadId on it.
///   5. Save, store attachments, tell the assigned agent about a reply, publish events.
///
/// The "we received your email" no-reply notice is NOT sent here any more: it is sent by TicketCreatedEventHandler
/// (so it can never break this webhook), for tickets raised by a registered contact.
/// </summary>
public record IngestInboundEmailCommand : IRequest<IngestInboundEmailResult>
{
    /// <summary>Envelope recipients (who the mail system actually delivered to).</summary>
    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();

    public string SenderEmail { get; init; } = string.Empty;
    public string? SenderName { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string? BodyHtml { get; init; }
    public string? BodyText { get; init; }
    public string MessageId { get; init; } = string.Empty;
    public string? InReplyTo { get; init; }
    public string? References { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<InboundAttachment> Attachments { get; init; } =
        Array.Empty<InboundAttachment>();
}

public class IngestInboundEmailCommandHandler : IRequestHandler<IngestInboundEmailCommand, IngestInboundEmailResult>
{
    private const int MaxSubjectLength = 300;   // keep <= your Ticket.Subject column length
    private const int MaxNameLength = 200;

    private readonly IApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ITicketNumberGenerator _ticketNumberGenerator;
    private readonly ITicketPriorityClassifier _priorityClassifier;
    private readonly ITicketAssignmentService _assignmentService;
    private readonly ISlaCalculator _slaCalculator;
    private readonly IHtmlSanitizerService _htmlSanitizer;
    private readonly IDateTime _dateTime;
    private readonly IPublisher _publisher;
    private readonly InboundEmailOptions _options;
    private readonly ILogger<IngestInboundEmailCommandHandler> _logger;
    private readonly IAttachmentStorage _attachmentStorage;
    private readonly INotificationEmailService _notificationEmailService;

    public IngestInboundEmailCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ITicketNumberGenerator ticketNumberGenerator,
        ITicketPriorityClassifier priorityClassifier,
        ITicketAssignmentService assignmentService,
        ISlaCalculator slaCalculator,
        IHtmlSanitizerService htmlSanitizer,
        IDateTime dateTime,
        IPublisher publisher,
        IOptions<InboundEmailOptions> options,
        ILogger<IngestInboundEmailCommandHandler> logger,
        IAttachmentStorage attachmentStorage,
        INotificationEmailService notificationEmailService)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _ticketNumberGenerator = ticketNumberGenerator;
        _priorityClassifier = priorityClassifier;
        _assignmentService = assignmentService;
        _slaCalculator = slaCalculator;
        _htmlSanitizer = htmlSanitizer;
        _dateTime = dateTime;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
        _attachmentStorage = attachmentStorage;
        _notificationEmailService = notificationEmailService;
    }

    public async Task<IngestInboundEmailResult> Handle(IngestInboundEmailCommand request, CancellationToken ct)
    {
        // ---- 1. Basic validation -------------------------------------------------
        var sender = InboundEmailAddress.Parse(request.SenderEmail);
        var messageId = MessageIdHelper.Normalize(request.MessageId);

        if (sender is null || messageId.Length == 0)
        {
            return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredInvalid);
        }

        var senderEmail = sender.Value.Email;
        var senderName = Truncate(request.SenderName ?? sender.Value.Name, MaxNameLength);

        // ---- 2. Robots: never turn auto-replies into tickets ---------------------
        if (AutoReplyDetector.IsAutoGenerated(request.Headers, senderEmail))
        {
            return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredAutoGenerated);
        }

        // ---- 3. Which workspace? ENVELOPE RECIPIENT ONLY -------------------------
        // Never trust anything in the body/subject/From to pick the workspace.
        var candidates = request.Recipients
            .Select(r => InboundEmailAddress.Parse(r)?.Email)
            .Where(e => e is not null)
            .SelectMany(e => new[] { e!, InboundEmailAddress.StripSubaddress(e!) })   // exact first, then without +tag
            .Distinct()
            .ToList();

        if (candidates.Count == 0)
        {
            return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredUnknownMailbox);
        }

        // Cross-tenant lookup by design: we don't know the tenant yet.
        var mailboxes = await _context.Mailboxes
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => candidates.Contains(m.EmailAddress) && m.IsVerified)
            .ToListAsync(ct);

        var mailbox = candidates
            .Select(c => mailboxes.FirstOrDefault(m => m.EmailAddress == c))
            .FirstOrDefault(m => m is not null);

        if (mailbox is null)
        {
            // Typo'd or unknown address: log + 200. An error would make the
            // provider retry and eventually disable the webhook.
            _logger.LogInformation("Inbound email to unknown mailbox ignored. Recipients: {Recipients}",
                string.Join(", ", candidates));
            return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredUnknownMailbox);
        }

        var tenantId = mailbox.TenantId;

        var tenant = await _context.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null || tenant.Status != TenantStatus.Active)
        {
            return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredUnknownMailbox);
        }

        // RLS: there's no JWT on this public endpoint, so tell the DB session
        // which tenant we resolved. Everything after this line is scoped to it.
        _currentTenantService.SetTenant(tenantId);

        // ---- 4. De-duplicate (SCOPED TO THIS TENANT) -----------------------------
        var duplicate = await FindExistingMessageAsync(tenantId, messageId, ct);
        if (duplicate is not null)
        {
            return duplicate;
        }

        // ---- 5. Body: prefer HTML, fall back to plain text, cap, sanitize --------
        var rawHtml = !string.IsNullOrWhiteSpace(request.BodyHtml)
            ? request.BodyHtml!
            : TextToHtml(request.BodyText);

        if (rawHtml.Length > _options.MaxBodyChars)
        {
            rawHtml = rawHtml[.._options.MaxBodyChars] + "<p><em>[Message truncated]</em></p>";
        }

        var sanitizedBody = _htmlSanitizer.Sanitize(rawHtml);   // XSS protection
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? "(no subject)"
            : Truncate(request.Subject.Trim(), MaxSubjectLength)!;
        var now = _dateTime.Now;

        // ---- 6. THE THREAD: does this email's threadId already belong to a ticket? ---------------
        var contact = await _context.OrganizationContacts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Email == senderEmail, ct);

        // 6a. STRONGEST SIGNAL: the customer replied to the signed reply address of one of our emails
        //     (gs1+gs-000114.<signature>@...). It does not depend on any Message-ID, which the email provider rewrites.
        var ticket = await FindTicketByReplyAddressAsync(tenantId, request.Recipients, ct);

        // 6b. Otherwise the thread id decides: already known = a reply, new = a new ticket.
        var threadId = ResolveThreadId(request, messageId);
        ticket ??= await FindTicketByThreadAsync(tenantId, request, threadId, ct);

        var events = new List<INotification>();
        bool isNewTicket;

        if (ticket is not null)
        {
            // ---- A REPLY: it joins the existing ticket's conversation. No new ticket. ------------
            isNewTicket = false;

            if (ticket.Status is TicketStatus.Resolved or TicketStatus.Closed)
            {
                var previousStatus = ticket.Status;
                ticket.Status = TicketStatus.Open;
                ticket.ClosedAt = null;

                _context.TicketStatusHistories.Add(new TicketStatusHistory
                {
                    TenantId = tenantId,
                    TicketId = ticket.Id,
                    FromStatus = previousStatus,
                    ToStatus = TicketStatus.Open,
                    ChangedAt = now,
                    Note = "Reopened by customer reply"
                });

                events.Add(new TicketStatusChangedEvent(
                    tenantId, ticket.Id, ticket.TicketNumber, previousStatus, TicketStatus.Open));

                // Auto-assign to its last owner; only resolve a new one if it has none.
                ticket.AssignedToTeamMemberId ??= await _assignmentService.ResolveAssigneeAsync(
                    tenantId, ticket.OrganizationId, ticket.OrganizationDepartmentId,
                    ticket.OrganizationContactId, ct);
            }
        }
        else
        {
            // ---- A NEW THREAD: raise a new ticket and store the threadId on it. ------------------
            isNewTicket = true;

            // Flood guard: spam waves / robot loops can't create unlimited tickets.
            var since = now.AddHours(-1);
            var recentFromSender = await _context.Tickets
                .IgnoreQueryFilters()
                .CountAsync(t => t.TenantId == tenantId && t.RawSenderEmail == senderEmail && t.CreatedAt >= since, ct);

            if (recentFromSender >= _options.MaxNewTicketsPerSenderPerHour)
            {
                _logger.LogWarning("Rate limit hit for sender {Sender} in tenant {Tenant}", senderEmail, tenantId);
                return IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredRateLimited);
            }

            ticket = contact is not null
                ? await CreateTicketAsync(tenant, mailbox, threadId, subject, sanitizedBody, senderEmail, now,
                    TicketStatus.New, contact.OrganizationId, contact.OrganizationDepartmentId, contact.Id,
                    runAssignment: true, ct)
                // Unknown sender: never dropped - becomes an Unverified ticket,
                // kept out of normal queues, assignment presets skipped.
                : await CreateTicketAsync(tenant, mailbox, threadId, subject, sanitizedBody, senderEmail, now,
                    TicketStatus.Unverified, null, null, null,
                    runAssignment: false, ct);
        }

        var message = new TicketMessage
        {
            TenantId = tenantId,
            Ticket = ticket,
            TicketId = ticket.Id,
            Direction = MessageDirection.Inbound,
            AuthorType = MessageAuthorType.OrganizationContact,
            AuthorEmail = senderEmail,
            AuthorName = senderName,
            Body = sanitizedBody,
            MessageId = messageId,
            InReplyToMessageId = string.IsNullOrWhiteSpace(request.InReplyTo)
                ? null
                : MessageIdHelper.Normalize(request.InReplyTo)
        };

        _context.TicketMessages.Add(message);

        // ---- 7. Save (guard against two retries racing each other) --------------
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent retry may have inserted the same Message-ID first (or the same threadId) - see the unique
            // indexes in the migration notes. If so, treat as a duplicate; otherwise let the provider retry the
            // webhook (the retry will then find the ticket by its threadId and append).
            var raced = await FindExistingMessageAsync(tenantId, messageId, ct);
            if (raced is not null) return raced;
            throw;
        }

        await SaveAttachmentsAsync(tenantId, ticket.Id, message.Id, request.Attachments, ct);

        // ---- 8. A reply on an existing ticket: tell the assigned agent (in-app + email) ----------
        if (!isNewTicket)
        {
            await NotifyAssigneeOfReplyAsync(ticket, senderName ?? senderEmail, ct);
        }

        // ---- 9. Events AFTER the data is committed ------------------------------
        if (isNewTicket)
        {
            events.Add(new TicketCreatedEvent(
                tenantId, ticket.Id, ticket.TicketNumber, ticket.Subject, null, ticket.Status == TicketStatus.Unverified));

            if (ticket.AssignedToTeamMemberId.HasValue)
            {
                events.Add(new TicketAssignedEvent(
                    tenantId, ticket.Id, ticket.TicketNumber, ticket.AssignedToTeamMemberId.Value));
            }
        }

        foreach (var evt in events)
        {
            try
            {
                await _publisher.Publish(evt, ct);
            }
            catch (Exception ex)
            {
                // The ticket is safely saved; a failing notification must not
                // turn into HTTP 500 (which would make the provider retry).
                _logger.LogError(ex, "Publishing {Event} failed for ticket {Ticket}",
                    evt.GetType().Name, ticket.TicketNumber);
            }
        }

        return new IngestInboundEmailResult(
            isNewTicket ? InboundEmailOutcome.TicketCreated : InboundEmailOutcome.MessageAppended,
            ticket.Id, ticket.TicketNumber, message.Id, ticket.Status == TicketStatus.Unverified);
    }

    // =====================================================================================================
    //  THREAD LOGIC
    // =====================================================================================================

    /// <summary>
    /// The thread an email belongs to = the id of the FIRST email of that thread:
    ///   - the first id in References (mail apps list the thread's root first), else
    ///   - In-Reply-To (a direct reply), else
    ///   - the email's OWN Message-ID (nothing points back, so this email starts a new thread).
    /// </summary>
    private static string ResolveThreadId(IngestInboundEmailCommand request, string ownMessageId)
    {
        var firstReference = SplitIds(request.References).FirstOrDefault();
        if (!string.IsNullOrEmpty(firstReference)) return firstReference;

        var inReplyTo = SplitIds(request.InReplyTo).FirstOrDefault();
        return !string.IsNullOrEmpty(inReplyTo) ? inReplyTo : ownMessageId;
    }

    /// <summary>
    /// The signed reply address (see ReplyAddress). Valid signature = this email is for that ticket, full stop.
    /// Returns null when no recipient carries a valid tag, or when InboundEmail:ReplyTokenSecret is not configured.
    /// </summary>
    private async Task<Ticket?> FindTicketByReplyAddressAsync(
        Guid tenantId, IReadOnlyList<string> recipients, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ReplyTokenSecret)) return null;

        foreach (var recipient in recipients)
        {
            var address = InboundEmailAddress.Parse(recipient)?.Email;
            if (address is null) continue;

            var number = ReplyAddress.TryGetTicketNumber(address, tenantId, _options.ReplyTokenSecret);
            if (number is null) continue;

            var ticket = await _context.Tickets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TicketNumber == number, ct);

            if (ticket is not null) return ticket;
        }

        return null;
    }

    /// <summary>
    /// 1) THE RULE: a ticket in this workspace already has this threadId -> that ticket.
    /// 2) Safety net: some mail apps drop References, or the provider rewrites the id of OUR email. If any message-id this
    ///    email points at is one we stored on a ticket's message, it is still a reply to that ticket - and an old ticket
    ///    that has no threadId yet gets one.
    /// There is no subject-line matching: a new thread is always a new ticket.
    /// </summary>
    private async Task<Ticket?> FindTicketByThreadAsync(
        Guid tenantId, IngestInboundEmailCommand request, string threadId, CancellationToken ct)
    {
        var byThread = await _context.Tickets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.ThreadId == threadId, ct);

        if (byThread is not null) return byThread;

        var ids = SplitIds(request.References);
        foreach (var id in SplitIds(request.InReplyTo))
        {
            if (!ids.Contains(id)) ids.Add(id);
        }

        if (ids.Count == 0) return null;

        var byMessage = await _context.TicketMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.MessageId != null && ids.Contains(m.MessageId))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Ticket!)
            .FirstOrDefaultAsync(ct);

        if (byMessage is not null && string.IsNullOrEmpty(byMessage.ThreadId))
        {
            byMessage.ThreadId = threadId;   // an older ticket learns its thread the first time the customer replies
        }

        return byMessage;
    }

    /// <summary>Every id in a References / In-Reply-To value, whatever shape the provider delivered it in
    /// ("&lt;a&gt; &lt;b&gt;", "&lt;a&gt;, &lt;b&gt;" or a JSON-looking list ["&lt;a&gt;","&lt;b&gt;"]).</summary>
    private static List<string> SplitIds(string? raw) => MessageIdHelper.ExtractAll(raw);

    private async Task<IngestInboundEmailResult?> FindExistingMessageAsync(
        Guid tenantId, string messageId, CancellationToken ct)
    {
        var existing = await _context.TicketMessages
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.MessageId == messageId)
            .Select(m => new { m.Id, m.TicketId, m.Ticket!.TicketNumber, m.Ticket.Status })
            .FirstOrDefaultAsync(ct);

        return existing is null
            ? null
            : new IngestInboundEmailResult(
                InboundEmailOutcome.Duplicate, existing.TicketId, existing.TicketNumber, existing.Id,
                existing.Status == TicketStatus.Unverified);
    }

    private async Task<Ticket> CreateTicketAsync(
        Tenant tenant, Mailbox mailbox, string threadId, string subject, string sanitizedBody, string senderEmail,
        DateTime now, TicketStatus status, Guid? organizationId, Guid? organizationDepartmentId,
        Guid? organizationContactId, bool runAssignment, CancellationToken ct)
    {
        var priority = _priorityClassifier.Classify(subject, sanitizedBody);
        var dueAt = await _slaCalculator.CalculateDueDateAsync(tenant.Id, priority, now, ct);

        Guid? assignedTo = null;
        if (runAssignment)
        {
            assignedTo = await _assignmentService.ResolveAssigneeAsync(
                tenant.Id, organizationId, organizationDepartmentId, organizationContactId, ct);
        }

        var ticket = new Ticket
        {
            TenantId = tenant.Id,
            TicketNumber = await _ticketNumberGenerator.NextAsync(tenant.TicketPrefix, ct),
            ThreadId = threadId,                       // <- the ticket's one mail thread
            OrganizationId = organizationId,
            OrganizationDepartmentId = organizationDepartmentId,
            OrganizationContactId = organizationContactId,
            RawSenderEmail = senderEmail,
            MailboxId = mailbox.Id,
            Subject = subject,
            Status = status,
            Priority = priority,
            Source = TicketSource.Email,
            AssignedToTeamMemberId = assignedTo,
            DueAt = dueAt
        };

        // A brand-new ticket has no "previous" status, so FromStatus mirrors ToStatus.
        ticket.StatusHistory.Add(new TicketStatusHistory
        {
            TenantId = tenant.Id,
            TicketId = ticket.Id,
            FromStatus = status,
            ToStatus = status,
            ChangedAt = now,
            Note = status == TicketStatus.Unverified ? "Created from unrecognized sender" : "Created from inbound email"
        });

        _context.Tickets.Add(ticket);
        return ticket;
    }

    // =====================================================================================================
    //  REPLY NOTIFICATION
    // =====================================================================================================

    /// <summary>In-app notification + email to the agent the ticket is assigned to. Never throws.</summary>
    private async Task NotifyAssigneeOfReplyAsync(Ticket ticket, string customerLabel, CancellationToken ct)
    {
        if (!ticket.AssignedToTeamMemberId.HasValue) return;

        try
        {
            var assignee = await _context.TeamMembers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == ticket.AssignedToTeamMemberId.Value && m.TenantId == ticket.TenantId, ct);

            if (assignee is null) return;

            _context.Notifications.Add(new Notification
            {
                TenantId = ticket.TenantId,
                TeamMemberId = assignee.Id,
                TicketId = ticket.Id,
                Title = "Customer replied",
                Message = $"{customerLabel} replied on ticket {ticket.TicketNumber}.",
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(ct);

            if (!string.IsNullOrWhiteSpace(assignee.Email))
            {
                await _notificationEmailService.SendCustomerRepliedAsync(
                    assignee.Email, assignee.FullName, ticket.TicketNumber, ticket.Subject, customerLabel, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not notify the assignee about the reply on ticket {Ticket}", ticket.TicketNumber);
        }
    }

    // =====================================================================================================
    //  HELPERS
    // =====================================================================================================

    private static string TextToHtml(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? "<p>(empty message)</p>"
            : "<p>" + WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>") + "</p>";

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private async Task SaveAttachmentsAsync(
        Guid tenantId, Guid ticketId, Guid messageId,
        IReadOnlyList<InboundAttachment> attachments, CancellationToken ct)
    {
        if (attachments.Count == 0) return;

        var added = 0;
        foreach (var file in attachments)
        {
            try
            {
                var fileName = AttachmentRules.SafeFileName(file.FileName);

                if (AttachmentRules.IsBlocked(fileName))
                {
                    _logger.LogWarning("Blocked attachment {File} on ticket {Ticket}", fileName, ticketId);
                    continue;
                }

                var key = await _attachmentStorage.SaveAsync(tenantId, ticketId, fileName, file.Content, ct);

                _context.TicketAttachments.Add(new TicketAttachment
                {
                    TenantId = tenantId,
                    TicketId = ticketId,
                    TicketMessageId = messageId,
                    FileName = fileName,
                    ContentType = file.ContentType,
                    SizeBytes = file.Content.LongLength,
                    StorageKey = key,
                    ContentId = file.ContentId,
                    IsInline = file.IsInline
                });
                added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The ticket and message are already saved; a bad file must not undo that.
                _logger.LogError(ex, "Could not store attachment {File} for ticket {Ticket}", file.FileName, ticketId);
            }
        }

        if (added == 0) return;

        try { await _context.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not save attachment records for ticket {Ticket}", ticketId);
        }
    }
}