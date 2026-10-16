using POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;
using System;
using System.Collections.Generic;
using System.Text;

public class TicketThreadDto
{
    public Guid TicketId { get; set; }

    public string TicketNumber { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public Guid? MailboxId { get; set; }

    public string? MailboxAddress { get; set; }

    public bool IsEmailTicket { get; set; }

    public List<TicketConversationDto> Messages { get; set; } = new();
}
