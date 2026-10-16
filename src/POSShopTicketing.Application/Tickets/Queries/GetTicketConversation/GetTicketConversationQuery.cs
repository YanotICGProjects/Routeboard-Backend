using MediatR;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;

public record GetTicketConversationQuery(string TicketNumber)
    : IRequest<List<TicketConversationDto>>;