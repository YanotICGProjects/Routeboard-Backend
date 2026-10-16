using System.Net;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.Common.Services;

/// <summary>
/// REPLACES Application/Common/Services/NotificationEmailService.cs (the whole file).
/// INotificationEmailService is unchanged, so nothing that calls it needs editing.
///
/// What changed:
///   1. Every automatic email now goes through ITicketEmailService as a NOTICE. That means each one is tied to
///      an existing ticket, comes from no-reply__{workspace}@support.routeboard.online, and carries the ticket
///      number in the subject - the rules from the call, enforced in one place.
///   2. Values from customers (the ticket subject!) are HTML-encoded before going into the email. Before, a
///      subject like "&lt;b&gt;hi&lt;/b&gt;" was dropped into the email HTML as-is.
///   3. A failure is logged, never thrown: a notice that couldn't be sent must not break the request that
///      triggered it (for example the inbound webhook creating the ticket).
///   4. The "we received your email" notice no longer says "reply directly to this email" - it comes from a
///      no-reply address. It says to reply to the ORIGINAL conversation instead.
/// </summary>
public class NotificationEmailService : INotificationEmailService
{
    private readonly ITicketEmailService _ticketEmail;
    private readonly ILogger<NotificationEmailService> _logger;

    public NotificationEmailService(ITicketEmailService ticketEmail, ILogger<NotificationEmailService> logger)
    {
        _ticketEmail = ticketEmail;
        _logger = logger;
    }

    /// <summary>A ticket was created by hand (an agent raised it for the customer).</summary>
    public Task SendTicketCreatedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"Ticket created: {subject}",
            Page("Ticket created",
                "A ticket has been created for you. A member of our team will be in touch with you shortly.",
                ticketNumber, subject),
            ct);

    /// <summary>Goes to the TEAM MEMBER the ticket was assigned to (not the customer).</summary>
    public Task SendTicketAssignedAsync(
        string recipientEmail, string? recipientName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(recipientEmail, recipientName, ticketNumber,
            $"Ticket assigned to you: {subject}",
            Page("Ticket assigned",
                "This ticket has been assigned to you.",
                ticketNumber, subject),
            ct);

    public Task SendTicketClosedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"Ticket closed: {subject}",
            Page("Ticket closed",
                "Your ticket has been closed. We hope your issue has been resolved. If it hasn't, reply to your " +
                "original email conversation about this ticket and we will reopen it for you.",
                ticketNumber, subject),
            ct);

    /// <summary>"We received your email" - sent when a customer's email created a ticket.</summary>
    /// <param name="replyTo">
    /// Kept so existing callers still compile, but no longer used: this is a no-reply message, and the server
    /// chooses the sender.
    /// </param>
    public Task SendAutoReplyAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, string replyTo,
        CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"We received your email: {subject}",
            Page("We've received your message",
                "Thank you for contacting us. We have received your email and created a ticket for it. " +
                "A member of our team will be in touch shortly. To add more information, reply to your original " +
                "email conversation.",
                ticketNumber, subject),
            ct);

    // -----------------------------------------------------------------------------------------------------

    private async Task SendNoticeAsync(
        string toEmail, string? toName, string ticketNumber, string subject, string html, CancellationToken ct)
    {
        try
        {
            await _ticketEmail.SendAsync(new TicketEmailRequest(
                TicketNumber: ticketNumber,
                Kind: TicketEmailKind.Notice,
                ToEmail: toEmail,
                ToName: toName,
                Subject: subject,
                Html: html), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the automatic email for ticket {Ticket} to {To}", ticketNumber, toEmail);
        }
    }

    /// <summary>Every notice names the ticket (number + subject), so the customer always knows what it is about.</summary>
    private static string Page(string heading, string text, string ticketNumber, string subject) => $@"
<div style=""font-family:Arial,sans-serif;font-size:14px;line-height:1.6;color:#333333;max-width:560px;"">
    <h2 style=""margin:0 0 12px 0;"">{Enc(heading)}</h2>
    <p>{Enc(text)}</p>
    <p>
        Ticket number: <strong>{Enc(ticketNumber)}</strong><br />
        Subject: <strong>{Enc(subject)}</strong>
    </p>
    <p style=""color:#666666;font-size:12px;"">
        This is an automatic message about ticket {Enc(ticketNumber)}. Please do not reply to this email.
    </p>
</div>";

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}