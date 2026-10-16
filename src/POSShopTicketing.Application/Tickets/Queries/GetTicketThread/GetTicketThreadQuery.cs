using MediatR;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketThread;

public record GetTicketThreadQuery(string TicketNumber)
    : IRequest<TicketThreadDto>;