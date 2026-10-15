namespace POSShopTicketing.Application.Common.Models;

/// <summary>
/// Everything IEmailSender needs to send one threaded reply.
/// </summary>
public record OutboundEmailMessage(
    string ToEmail,
    string? ToName,

    // Mailbox that owns the ticket
    string FromEmail,

    // Friendly display name
    string? FromName,

    // Where customer replies should go
    string FromAlias,

    string Subject,
    string BodyHtml,

    string? InReplyToMessageId,

    // Our generated Message-ID
    string GeneratedMessageId);