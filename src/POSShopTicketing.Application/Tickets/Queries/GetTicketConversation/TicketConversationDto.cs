using POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketConversation
{
    public class TicketConversationDto
    {
        public Guid Id { get; set; }

        public DateTime CreatedAt { get; set; }

        public string Direction { get; set; } = string.Empty;

        public string AuthorType { get; set; } = string.Empty;

        public string? AuthorName { get; set; }

        public string? AuthorEmail { get; set; }

        public string Body { get; set; } = string.Empty;

        public string? MessageId { get; set; }

        public string? InReplyToMessageId { get; set; }
        public List<TicketAttachmentDto> Attachments { get; set; } = new();
    }
}
