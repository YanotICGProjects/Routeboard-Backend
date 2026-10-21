using System.Net;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;   // TicketEmailKind (as in your solution)

namespace POSShopTicketing.Application.Common.Services;

/// <summary>
/// REPLACES Application/Common/Services/NotificationEmailService.cs (the whole file).
///
/// Every automatic email goes through ITicketEmailService as a NOTICE, so each one is tied to an existing ticket,
/// comes from the workspace's no-reply address and carries the ticket number and subject.
///
///   - Values that came from a customer (the ticket subject, their name) are HTML-encoded before they go into the
///     email. Before, they were pasted in as raw HTML.
///   - A failure is logged, never thrown: a notice must not break the request that triggered it.
///   - The "we received your email" notice no longer says "reply directly to this email" - it comes from a no-reply
///     address. It says to reply to the ORIGINAL email conversation instead.
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

    public Task SendTicketCreatedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"Ticket created: {subject}",
            Page("Ticket created", customerName,
                "A support ticket has been created for you. A member of our team will be in touch with you shortly.",
                ticketNumber, subject),
            ct);

    public Task SendTicketAssignedAsync(
        string recipientEmail, string? recipientName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(recipientEmail, recipientName, ticketNumber,
            $"Ticket assigned to you: {subject}",
            Page("Ticket assigned", recipientName,
                "This ticket has been assigned to you.",
                ticketNumber, subject),
            ct);

    public Task SendTicketClosedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"Ticket closed: {subject}",
            Page("Ticket closed", customerName,
                "Your ticket has been closed. We hope your issue has been resolved. If it hasn't, reply to your " +
                "original email conversation about this ticket and we will reopen it for you.",
                ticketNumber, subject),
            ct);

    public Task SendAutoReplyAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct) =>
        SendNoticeAsync(customerEmail, customerName, ticketNumber,
            $"We received your email: {subject}",
            Page("We've received your email", customerName,
                "Thank you for contacting us. We have received your email and created a ticket for it. " +
                "A member of our team will be in touch shortly. To add more information, reply to your original " +
                "email conversation.",
                ticketNumber, subject),
            ct);

    public Task SendCustomerRepliedAsync(
        string recipientEmail, string? recipientName, string ticketNumber, string subject, string customerLabel,
        CancellationToken ct) =>
        SendNoticeAsync(recipientEmail, recipientName, ticketNumber,
            $"Customer replied: {subject}",
            Page("Customer replied", recipientName,
                $"{customerLabel} replied to this ticket. Open the ticket to read and answer the message.",
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

    /// <summary>Every notice names the ticket (number + subject), so the reader always knows what it is about.</summary>
    private static string Page(string heading, string? name, string text, string ticketNumber, string subject)
    {
        var greeting = string.IsNullOrWhiteSpace(name) ? "Hello," : $"Hello {Enc(name)},";

        return $@"
<div style=""font-family:Arial,sans-serif;font-size:14px;line-height:1.6;color:#333333;max-width:560px;"">
    <h2 style=""margin:0 0 12px 0;"">{Enc(heading)}</h2>
    <p>{greeting}</p>
    <p>{Enc(text)}</p>
    <p>
        Ticket number: <strong>{Enc(ticketNumber)}</strong><br />
        Subject: <strong>{Enc(subject)}</strong>
    </p>
    <p style=""color:#666666;font-size:12px;"">
        This is an automatic message about ticket {Enc(ticketNumber)}. Please do not reply to this email.
    </p>
</div>";
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}