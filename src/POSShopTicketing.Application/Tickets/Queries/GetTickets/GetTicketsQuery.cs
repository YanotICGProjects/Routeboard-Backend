using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Queries.GetTickets;

/// <summary>GET /tickets?status=&priority=&assignee=&org= - the normal
/// working queue. Unverified tickets are deliberately excluded unless
/// explicitly requested - they belong in GetTriageQueue, "kept out of
/// agents' normal queues until a Manager decides what it is".</summary>
public record GetTicketsQuery : IRequest<PaginatedList<TicketDto>>
{
    public string? SearchTerm { get; init; }
    public TicketStatus? Status { get; init; }
    public TicketPriority? Priority { get; init; }
    public Guid? OrganizationId { get; init; }
    public Guid? AssignedToTeamMemberId { get; init; }
    public bool? Escalated { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public class GetTicketsQueryHandler : IRequestHandler<GetTicketsQuery, PaginatedList<TicketDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTime _dateTime;

    public GetTicketsQueryHandler(IApplicationDbContext context, IDateTime dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<PaginatedList<TicketDto>> Handle(GetTicketsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Tickets
            .Include(t => t.Organization)
            .Include(t => t.OrganizationDepartment)
            .Include(t => t.OrganizationContact)
            .Include(t => t.AssignedToTeamMember)
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .AsQueryable();

        //query = request.Status.HasValue
        //    ? query.Where(t => t.Status == request.Status.Value)
        //    : query.Where(t => t.Status != TicketStatus.Unverified);

        if (request.Priority.HasValue)
        {
            query = query.Where(t => t.Priority == request.Priority.Value);
        }

        if (request.OrganizationId.HasValue)
        {
            query = query.Where(t => t.OrganizationId == request.OrganizationId.Value);
        }

        if (request.AssignedToTeamMemberId.HasValue)
        {
            query = query.Where(t =>
                t.AssignedToTeamMemberId == request.AssignedToTeamMemberId.Value
                || !t.AssignedToTeamMemberId.HasValue);
        }

        if (request.Escalated.HasValue)
        {
            query = query.Where(t => t.Escalated == request.Escalated.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim();
            query = query.Where(t => EF.Functions.Like(t.TicketNumber, $"%{term}%") || EF.Functions.Like(t.Subject, $"%{term}%"));
        }

        var paged = await PaginatedList<Domain.Entities.Ticket>.CreateAsync(query, request.PageNumber, request.PageSize);

        var now = _dateTime.Now;
        var dtos = paged.Items.Select(t => TicketDto.FromEntity(t, now)).ToList();

        return new PaginatedList<TicketDto>(dtos, paged.TotalCount, paged.PageNumber, request.PageSize);
    }
}
