using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment
{
    public class TicketAttachmentDto
    {
        public Guid Id { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string? ContentType { get; set; }

        public long SizeBytes { get; set; }
    }
}
