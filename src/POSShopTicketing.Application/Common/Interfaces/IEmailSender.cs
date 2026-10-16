using POSShopTicketing.Application.Common.Models;

namespace POSShopTicketing.Application.Common.Interfaces;

/// <summary>
/// Outbound email, in two shapes:
///   - SendReplyAsync: a ticket thread reply, with the per-ticket
///     Reply-To alias and In-Reply-To/References set so it threads
///     correctly in the customer's own mail client (see "Email
///     ingestion & threading" in the README).
///   - SendAsync: any other transactional email - activity/audit
///     notices (login, logout, ticket operations), invites, etc. - no
///     threading headers, just a plain message to a specific recipient.
///
/// Not tied to any one provider: the default implementation logs
/// instead of sending, and a generic SMTP implementation (works with
/// SendGrid, Postmark, Amazon SES, Mailgun, Gmail, Hostinger's own mail
/// hosting, or literally any other SMTP-capable provider) is available
/// behind the same interface - see README "Outbound email" and
/// Infrastructure/Services/SmtpEmailSender.cs.
/// </summary>
public interface IEmailSender
{
    Task SendReplyAsync(
        OutboundEmailMessage message,
        CancellationToken cancellationToken);

    Task SendAsync(
        string toEmail,
        string? toName,
        string subject,
        string bodyHtml,
        CancellationToken cancellationToken);

    Task SendAutoReplyAsync(
        string toEmail,
        string? toName,
        string subject,
        string bodyHtml,
        string replyTo,
        CancellationToken cancellationToken);
}
