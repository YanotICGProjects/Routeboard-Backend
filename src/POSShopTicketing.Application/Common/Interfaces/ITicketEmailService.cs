using POSShopTicketing.Domain.Entities;   // wherever TicketEmailKind lives in your solution (as in your current file)

namespace POSShopTicketing.Application.Common.Interfaces;

// =====================================================================
//  REPLACES Application/Common/Interfaces/ITicketEmailService.cs
//
//  THE RULES (from the call), now built around the THREAD ID
//  ----------------------------------------------------------
//   1. Every email the app sends to a customer is about ONE existing ticket and carries its ticket number.
//   2. This service NEVER creates a ticket. A ticket is created by an agent in the app, or by a customer's
//      email that starts a NEW mail thread (see IngestInboundEmailCommand).
//   3. ONE TICKET = ONE MAIL THREAD. The thread is identified by its threadId: the Message-ID of the first
//      email of the thread (normalized: no angle brackets, lowercase). It is stored on the ticket.
//        - A customer's email whose threadId already exists  -> a reply: added to that ticket's conversation.
//        - A customer's email with a new threadId            -> a new ticket.
//        - A ticket created by hand has NO threadId until the first message is emailed to the customer.
//   4. Two kinds of email:
//        Notice        automatic "no-reply" message. From no-reply__{workspace}@support.routeboard.online.
//                      A separate email: it never starts or joins the thread.
//        Conversation  an agent's message to the customer. From the workspace support address. The first
//                      Conversation email on a ticket STARTS the thread (stores the threadId); every later one
//                      continues it.
// =====================================================================

public record TicketEmailRequest(
    string TicketNumber,
    TicketEmailKind Kind,
    string ToEmail,
    string? ToName,
    string? Subject,
    string Html,
    string? FromEmail = null,                 // optional: if given it must be what the server would choose anyway
    string? FromName = null,
    string? ThreadId = null,                  // Conversation: empty for the first message, otherwise the ticket's own threadId
    Guid? ExistingTicketMessageId = null);    // INTERNAL use only (never set by the API): send an already-saved message

public record TicketEmailResult(
    TicketEmailKind Kind,
    Guid TicketId,
    string TicketNumber,

    /// <summary>The ticket's mail thread (Conversation only). Same for the whole life of the ticket.</summary>
    string? ThreadId,

    /// <summary>The conversation message saved in the ticket (Conversation only).</summary>
    Guid? TicketMessageId,

    /// <summary>This email's own Message-ID - what the customer's reply will point back at.</summary>
    string? MessageId,

    string? InReplyTo,
    string? References,

    /// <summary>What actually went out.</summary>
    string From,
    string Subject,

    /// <summary>Resend's id for the sent email.</summary>
    string? ResendId);

/// <summary>What the UI needs before showing the "send to customer" box.</summary>
public record TicketThreadInfo(
    Guid TicketId,
    string TicketNumber,

    /// <summary>False for a ticket created by hand that has never been emailed to the customer.</summary>
    bool HasThread,

    /// <summary>Null until the thread exists.</summary>
    string? ThreadId,

    /// <summary>The workspace support address a Conversation email will come from (null if the workspace has no mailbox).</summary>
    string? ConversationFrom,

    /// <summary>The address automatic notices come from.</summary>
    string NoReplyFrom,

    /// <summary>The subject the next Conversation email will carry.</summary>
    string NextSubject,

    string? CustomerEmail,
    string? CustomerName);

public interface ITicketEmailService
{
    /// <summary>
    /// Validates the ticket and the recipient, works out the sender and the thread, saves the conversation
    /// message (Conversation only) and sends the email.
    /// Throws NotFoundException (no such ticket), DomainException (a rule was broken) or
    /// EmailSendException (the email provider refused the message).
    /// </summary>
    Task<TicketEmailResult> SendAsync(TicketEmailRequest request, CancellationToken ct);

    /// <summary>The ticket's thread and the sender / subject the next Conversation email will use.</summary>
    Task<TicketThreadInfo> GetThreadAsync(string ticketNumber, CancellationToken ct);
}