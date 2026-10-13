using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Common
{
    /// <summary>
    /// One file that came with an inbound email, already downloaded into memory by the
    /// provider adapter (Resend/Postmark/...). Limits are enforced in the adapter.
    /// </summary>
    public record InboundAttachment(
        string FileName,
        string ContentType,
        byte[] Content,
        string? ContentId,
        bool IsInline);
}
