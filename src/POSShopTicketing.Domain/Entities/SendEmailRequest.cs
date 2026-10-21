using System.ComponentModel.DataAnnotations;
using POSShopTicketing.Domain.Entities;   // wherever TicketEmailKind lives in your solution (as in your current file)

namespace POSShopTicketing.Api.Models.Email;

/// <summary>
/// REPLACES Api/Models/Email/SendEmailRequest.cs. One payload shape for both kinds of email.
/// "mailboxId" is gone: the identifier that matters is the THREAD ID, and the server already knows both the thread
/// and the workspace support address from the ticket.
///
/// 1. AUTOMATIC NOTICE (ticket created / assigned / closed ...) - from the no-reply address, never in the thread:
///    { "toEmail": "...", "toName": "...", "subject": "...", "html": "...",
///      "fromEmail": "no-reply__pos-shop@support.routeboard.online", "fromName": "No Reply",
///      "messageTicketNumber": "POSSH-000078" }
///
/// 2. FIRST CONVERSATION EMAIL on a ticket created by hand (the ticket has no thread yet) - from the workspace
///    support address; it STARTS the thread and the response returns the new threadId:
///    { "toEmail": "...", "toName": "...", "html": "...", "fromEmail": "pos-shop@support.routeboard.online",
///      "fromName": "POS Shop", "messageTicketNumber": "POSSH-000078", "threadId": "" }
///
/// 3. LATER CONVERSATION EMAILS (and replies to a ticket the customer raised by email) - they continue the ticket's
///    existing thread and never create a ticket:
///    { ...as above..., "threadId": "the-ticket's-threadId" }
/// </summary>
public class SendEmailRequest
{
    [Required, EmailAddress]
    public string ToEmail { get; set; } = string.Empty;

    public string? ToName { get; set; }

    /// <summary>
    /// Notice: the subject (the ticket number is added if it is missing).
    /// Conversation: ignored - the server uses the ticket's own subject so the email stays in the same thread.
    /// </summary>
    public string? Subject { get; set; }

    [Required]
    public string Html { get; set; } = string.Empty;

    /// <summary>Optional. If given it must be the address the server would choose itself; anything else is refused.</summary>
    public string? FromEmail { get; set; }

    public string? FromName { get; set; }

    /// <summary>Accepted for compatibility but not used: the server decides the Reply-To.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>The ticket this email is about, e.g. "POSSH-000078". REQUIRED. The ticket must already exist.</summary>
    [Required]
    public string MessageTicketNumber { get; set; } = string.Empty;

    /// <summary>
    /// The ticket's mail thread. Empty / missing for the FIRST message to the customer on a ticket that has no thread
    /// yet. If the ticket already has a thread, this must be that thread (or empty).
    /// </summary>
    [MaxLength(500)]
    [RegularExpression(@"^[^\s<>]*$", ErrorMessage = "threadId must not contain spaces or angle brackets.")]
    public string? ThreadId { get; set; }

    /// <summary>"Notice" or "Conversation". Optional: if missing it is worked out from fromEmail (no-reply... = Notice).</summary>
    public TicketEmailKind? Type { get; set; }
}