using MediatR;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Events;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Commands.ChangeTicketAssignee;

/// <summary>PATCH /tickets/{id}/assignee - manual (re)assignment. Per
/// the roles table, a Manager can reassign any ticket on their team;
/// an Agent cannot (enforced at the controller).</summary>
public record ChangeTicketAssigneeCommand(Guid TicketId, Guid? AssignedToTeamMemberId) : IRequest;

public class ChangeTicketAssigneeCommandHandler : IRequestHandler<ChangeTicketAssigneeCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly IPublisher _publisher;
    private readonly ICurrentUserService _currentUserService;

    public ChangeTicketAssigneeCommandHandler(IApplicationDbContext context, IPublisher publisher, ICurrentUserService currentUserService)
    {
        _context = context;
        _publisher = publisher;
        _currentUserService = currentUserService;
    }

    public async Task Handle(
    ChangeTicketAssigneeCommand request,
    CancellationToken cancellationToken)
    {
        var ticket = await _context.Tickets.FindAsync(
            new object[] { request.TicketId },
            cancellationToken)
            ?? throw new NotFoundException(nameof(Ticket), request.TicketId);

        if (request.AssignedToTeamMemberId.HasValue)
        {
            var agent = await _context.TeamMembers.FindAsync(
                new object[] { request.AssignedToTeamMemberId.Value },
                cancellationToken)
                ?? throw new NotFoundException(
                    nameof(TeamMember),
                    request.AssignedToTeamMemberId.Value);

            if (agent.Status != TeamMemberStatus.Active)
            {
                throw new NotFoundException(
                    nameof(TeamMember),
                    request.AssignedToTeamMemberId.Value);
            }
        }

        ticket.AssignedToTeamMemberId = request.AssignedToTeamMemberId;

        await _context.SaveChangesAsync(cancellationToken);

        // SELF ASSIGNMENT => NO EMAIL, NO NOTIFICATION
        if (request.AssignedToTeamMemberId.HasValue &&
            request.AssignedToTeamMemberId.Value ==
            _currentUserService.TeamMemberId)
        {
            return;
        }

        if (request.AssignedToTeamMemberId.HasValue)
        {
            await _publisher.Publish(
                new TicketAssignedEvent(
                    ticket.TenantId,
                    ticket.Id,
                    ticket.TicketNumber,
                    request.AssignedToTeamMemberId.Value,
                    _currentUserService.TeamMemberId!.Value),
                cancellationToken);
        }
    }
}
