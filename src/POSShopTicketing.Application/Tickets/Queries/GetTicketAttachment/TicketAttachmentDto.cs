using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment
{
    public record TicketAttachmentDto(
    Guid Id,
    Guid TicketMessageId,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool IsInline);
}
