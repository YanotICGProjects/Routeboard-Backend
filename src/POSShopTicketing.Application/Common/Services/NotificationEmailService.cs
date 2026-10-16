using POSShopTicketing.Application.Common.Interfaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Common.Services
{

    public class NotificationEmailService : INotificationEmailService
    {
        private readonly IEmailSender _emailSender;

        public NotificationEmailService(
            IEmailSender emailSender)
        {
            _emailSender = emailSender;
        }

        public async Task SendTicketCreatedAsync(
            string customerEmail,
            string? customerName,
            string ticketNumber,
            string subject,
            CancellationToken ct)
        {
            var html = $"""
        <h2>Ticket Created</h2>

        <p>Your ticket has been received.</p>

        <p>
            Ticket Number: <strong>{ticketNumber}</strong>
        </p>

        <p>
            Subject: <strong>{subject}</strong>
        </p>
        """;

            await _emailSender.SendAsync(
                customerEmail,
                customerName,
                $"Ticket Created - {ticketNumber}",
                html,
                ct);
        }

        public async Task SendTicketAssignedAsync(
            string recipientEmail,
            string? recipientName,
            string ticketNumber,
            string subject,
            CancellationToken ct)
        {
            var html = $"""
        <h2>Ticket Assigned</h2>

        <p>
            Ticket <strong>{ticketNumber}</strong>
            has been assigned to you.
        </p>

        <p>{subject}</p>
        """;

            await _emailSender.SendAsync(
                recipientEmail,
                recipientName,
                $"Ticket Assigned - {ticketNumber}",
                html,
                ct);
        }

        public async Task SendTicketClosedAsync(
            string customerEmail,
            string? customerName,
            string ticketNumber,
            string subject,
            CancellationToken ct)
        {
            var html = $"""
        <h2>Ticket Closed</h2>

        <p>
            Ticket <strong>{ticketNumber}</strong>
            has been closed.
        </p>

        <p>{subject}</p>
        """;

            await _emailSender.SendAsync(
                customerEmail,
                customerName,
                $"Ticket Closed - {ticketNumber}",
                html,
                ct);
        }

        public async Task SendAutoReplyAsync(
    string customerEmail,
    string? customerName,
    string ticketNumber,
    string subject,
    CancellationToken ct)
        {
            var html = $"""
    <h2>We've Received Your Message</h2>

    <p>Hello {(string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName)},</p>

    <p>
        Thank you for contacting us.
        Your request has been received and a support ticket has been created.
    </p>

    <p>
        Ticket Number:
        <strong>{ticketNumber}</strong>
    </p>

    <p>
        Subject:
        <strong>{subject}</strong>
    </p>

    <p>
        Our support team will get back to you shortly.
    </p>

    <p>
        Please reply directly to this email if you need to add more information.
    </p>
    """;

            await _emailSender.SendAsync(
                customerEmail,
                customerName,
                $"Re: [{ticketNumber}] {subject}",
                html,
                ct);
        }
    }
}
