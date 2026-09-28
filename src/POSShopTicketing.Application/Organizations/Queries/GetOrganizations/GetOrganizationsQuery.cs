using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.Organizations.Queries.GetOrganizations;

public record GetOrganizationsQuery : IRequest<PaginatedList<OrganizationDto>>
{
    public string? SearchTerm { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public class GetOrganizationsQueryHandler : IRequestHandler<GetOrganizationsQuery, PaginatedList<OrganizationDto>>
{
    private readonly IApplicationDbContext _context;

    public GetOrganizationsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PaginatedList<OrganizationDto>> Handle(
     GetOrganizationsQuery request,
     CancellationToken cancellationToken)
    {
        var query = _context.Organizations
            .Include(o => o.Departments)
            .Include(o => o.Contacts)
            .AsNoTracking()
            .OrderBy(o => o.Name)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim();

            query = query.Where(o =>
                EF.Functions.Like(o.Name, $"%{term}%"));
        }

        //var totalCount = await query.CountAsync(cancellationToken);

        //if (totalCount == 0)
        //{
        //    throw new NotFoundException(
        //        nameof(Organization),
        //        request.SearchTerm ?? "Search Criteria");
        //}

        var paged = await PaginatedList<Organization>
            .CreateAsync(query, request.PageNumber, request.PageSize);

        var dtos = paged.Items
            .Select(OrganizationDto.FromEntity)
            .ToList();

        return new PaginatedList<OrganizationDto>(
            dtos,
            paged.TotalCount,
            paged.PageNumber,
            request.PageSize);
    }
}
