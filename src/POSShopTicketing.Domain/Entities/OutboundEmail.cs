using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Domain.Entities
{
    /// <summary>A provider-neutral outgoing email, used by the test endpoint and internally.</summary>
    public record OutboundEmail(
        string ToEmail,
        string? ToName,
        string Subject,
        string Html,
        string? Text = null,
        string? FromEmail = null,
        string? FromName = null,
        string? ReplyTo = null,
        string? MessageId = null,      // OUR Message-ID (brackets optional)
        string? InReplyTo = null,
        string? References = null);

}
