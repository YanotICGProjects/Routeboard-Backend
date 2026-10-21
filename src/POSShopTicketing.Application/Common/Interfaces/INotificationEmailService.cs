namespace POSShopTicketing.Application.Common.Interfaces;

/// <summary>
/// REPLACES Application/Common/Interfaces/INotificationEmailService.cs.
///
/// Automatic NO-REPLY emails. Each one is about a single ticket (its number and subject are in the email),
/// comes from no-reply__{workspace}@support.routeboard.online, and is a separate email that never joins the
/// customer's mail thread. They are all sent through ITicketEmailService, so the ticket and recipient are checked.
///
/// Changes from your version:
///   - SendAutoReplyAsync no longer takes "replyTo": a no-reply email has no reply address.
///   - SendCustomerRepliedAsync is new: tells the assigned agent a customer wrote back.
/// A failure to send is logged and swallowed - an automatic notice must never break the request that triggered it.
/// </summary>
public interface INotificationEmailService
{
    /// <summary>A ticket was created by hand and the agent wrote nothing to the customer yet ("ticket created").</summary>
    Task SendTicketCreatedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct);

    /// <summary>Goes to the TEAM MEMBER the ticket was assigned to.</summary>
    Task SendTicketAssignedAsync(
        string recipientEmail, string? recipientName, string ticketNumber, string subject, CancellationToken ct);

    Task SendTicketClosedAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct);

    /// <summary>"We received your email and created ticket X" - the customer's own email started a new thread.</summary>
    Task SendAutoReplyAsync(
        string customerEmail, string? customerName, string ticketNumber, string subject, CancellationToken ct);

    /// <summary>Goes to the TEAM MEMBER assigned to the ticket: the customer replied in the thread.</summary>
    Task SendCustomerRepliedAsync(
        string recipientEmail, string? recipientName, string ticketNumber, string subject, string customerLabel,
        CancellationToken ct);
}