using MediatR;

namespace POSShopTicketing.Application.Tickets.Commands.ChangeTicketTransportation;

public record ChangeTicketTransportationCommand(
    Guid TicketId,
    string TransportationRoute,
    string TransportationAmount
) : IRequest;
