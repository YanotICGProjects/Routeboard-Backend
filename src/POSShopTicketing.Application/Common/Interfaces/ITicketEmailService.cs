using POSShopTicketing.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Common.Interfaces;

// =====================================================================
//  NEW FILE: Application/Common/Interfaces/ITicketEmailService.cs
//
//  THE RULES FROM THE CALL, in one place
//  ------------------------------------
//   1. Every email the application sends to a customer is about ONE existing ticket and carries its
//      ticket number (messageTicketId, e.g. "POSSH-000078"). No ticket number = nothing is sent.
//   2. This service NEVER creates a ticket. Tickets are created by an agent in the app, or by a customer's
//      first email (the inbound webhook). Here the ticket already exists.
//   3. Two kinds of email:
//        Notice        automatic "no-reply" message (ticket created / assigned / closed ...).
//                      From no-reply__{workspace}@support.routeboard.online. A separate email, not part of the thread.
//        Conversation  an agent's message from the ticket's Conversations tab. From the WORKSPACE address,
//                      inside the customer's one mail thread for that ticket.
//   4. One ticket = one mail thread. The customer's reply to a conversation email goes back into the same
//      ticket (the inbound handler matches the thread). A brand-new email from the customer is a new ticket.
// =====================================================================



public record TicketEmailRequest(
    string TicketNumber,
    TicketEmailKind Kind,
    string ToEmail,
    string? ToName,
    string? Subject,
    string Html,
    string? FromEmail = null,    // optional: if given, it must be the address the server would choose anyway
    string? FromName = null,
    Guid? MailboxId = null);     // Conversation only: used when the ticket has no mailbox yet (manual tickets)

public record TicketEmailResult(
    TicketEmailKind Kind,
    Guid TicketId,
    string TicketNumber,

    /// <summary>The mailbox the email was sent from (Conversation only). Also saved on the ticket if it had none.</summary>
    Guid? MailboxId,
    string? MailboxAddress,

    /// <summary>The conversation message saved in the ticket (Conversation only).</summary>
    Guid? TicketMessageId,

    /// <summary>The mail thread: its first message id. Same for the whole life of the ticket.</summary>
    string? ThreadId,

    /// <summary>This email's own Message-ID - what the customer's reply will point back at.</summary>
    string? MessageId,

    string? InReplyTo,
    string? References,

    /// <summary>What actually went out.</summary>
    string From,
    string Subject,

    /// <summary>Resend's id for the sent email.</summary>
    string? ResendId);

public interface ITicketEmailService
{
    /// <summary>
    /// Validates the ticket and the recipient, works out the sender, mailbox and thread, saves the
    /// conversation message (Conversation only) and sends the email.
    /// Throws NotFoundException (no such ticket), DomainException (a rule was broken) or
    /// EmailSendException (the email provider refused the message).
    /// </summary>
    Task<TicketEmailResult> SendAsync(TicketEmailRequest request, CancellationToken ct);
}
