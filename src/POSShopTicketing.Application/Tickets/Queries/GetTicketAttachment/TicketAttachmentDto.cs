using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment
{
    public record TicketAttachmentDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool IsInline);
}
