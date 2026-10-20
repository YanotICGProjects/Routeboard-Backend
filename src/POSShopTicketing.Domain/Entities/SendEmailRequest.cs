using POSShopTicketing.Domain.Entities;
using System.ComponentModel.DataAnnotations;


namespace POSShopTicketing.Api.Models.Email;

/// <summary>
/// REPLACES Api/Models/Email/SendEmailRequest.cs. One payload shape for both kinds of email.
///
/// 1. AUTOMATIC NOTICE (ticket created / assigned / closed ...):
///    { "toEmail": "...", "toName": "...", "subject": "...", "html": "...",
///      "fromEmail": "no-reply__pos-shop@support.routeboard.online", "fromName": "No Reply",
///      "replyTo": "no-reply__pos-shop@support.routeboard.online", "messageTicketId": "POSSH-000078" }
///
/// 2. CONVERSATION (an agent writing to the customer from the ticket):
///    { "toEmail": "...", "toName": "...", "subject": "...", "html": "...",
///      "fromEmail": "pos-shop@support.routeboard.online", "fromName": "POS Shop",
///      "replyTo": "pos-shop@support.routeboard.online", "messageTicketId": "POSSH-000078", "mailboxId": "" }
///    mailboxId is empty for a ticket created by hand whose first conversation this is; for a ticket the customer
///    raised by email it is the mailbox the ticket arrived on.
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
    /// A string, because the frontend sends "" when there is none (a Guid property cannot bind an empty string).
    /// Empty / missing is fine.
    /// </summary>
    public string? MailboxId { get; set; }

    /// <summary>"Notice" or "Conversation". Optional: if missing it is worked out from fromEmail (no-reply... = Notice).</summary>
    public TicketEmailKind? Type { get; set; }
}