using POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;
using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketThread
{
    public class TicketThreadDto
    {
        public Guid TicketId { get; set; }

        public string TicketNumber { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        public List<TicketConversationDto> Messages { get; set; } = new();
    }
}
