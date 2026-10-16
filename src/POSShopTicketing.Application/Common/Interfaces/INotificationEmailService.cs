using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Common.Interfaces
{
    public interface INotificationEmailService
    {
        Task SendTicketCreatedAsync(
            string customerEmail,
            string? customerName,
            string ticketNumber,
            string subject,
            CancellationToken ct);

        Task SendTicketAssignedAsync(
            string recipientEmail,
            string? recipientName,
            string ticketNumber,
            string subject,
            CancellationToken ct);

        Task SendTicketClosedAsync(
            string customerEmail,
            string? customerName,
            string ticketNumber,
            string subject,
            CancellationToken ct);

        Task SendAutoReplyAsync(
    string customerEmail,
    string? customerName,
    string ticketNumber,
    string subject,
    string replyTo,
    CancellationToken ct);
    }
}
