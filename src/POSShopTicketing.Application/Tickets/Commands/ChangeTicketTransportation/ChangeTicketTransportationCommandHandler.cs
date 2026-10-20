using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.Tickets.Commands.ChangeTicketTransportation;

public class ChangeTicketTransportationCommandHandler
    : IRequestHandler<ChangeTicketTransportationCommand>
{
    private readonly IApplicationDbContext _context;

    public ChangeTicketTransportationCommandHandler(
        IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task Handle(
        ChangeTicketTransportationCommand request,
        CancellationToken cancellationToken)
    {
        var ticket = await _context.Tickets
            .FirstOrDefaultAsync(
                x => x.Id == request.TicketId,
                cancellationToken);

        if (ticket is null)
        {
            throw new NotFoundException(
                nameof(Ticket),
                request.TicketId);
        }

        ticket.TransportationRoute =
            request.TransportationRoute?.Trim() ?? string.Empty;

        ticket.TransportationAmount =
            request.TransportationAmount?.Trim() ?? string.Empty;

        _context.TicketStatusHistories.Add(
    new TicketStatusHistory
    {
        TenantId = ticket.TenantId,
        TicketId = ticket.Id,
        FromStatus = ticket.Status,
        ToStatus = ticket.Status,
        ChangedAt = DateTime.UtcNow,
        Note =
            $"Transportation updated. Route: {request.TransportationRoute}, Amount: {request.TransportationAmount}"
    });

        await _context.SaveChangesAsync(
            cancellationToken);
    }
}