using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.OrganizationMembers.Queries.GetOrganizationMembers;

public record GetOrganizationContactsQuery : IRequest<PaginatedList<OrganizationContactDto>>
{
    public Guid? OrganizationId { get; init; }
    public string? SearchTerm { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

public class GetOrganizationContactsQueryHandler : IRequestHandler<GetOrganizationContactsQuery, PaginatedList<OrganizationContactDto>>
{
    private readonly IApplicationDbContext _context;

    public GetOrganizationContactsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PaginatedList<OrganizationContactDto>> Handle(
        GetOrganizationContactsQuery request,
        CancellationToken cancellationToken)
    {
        var organizationExists = await _context.Organizations
            .AsNoTracking()
            .AnyAsync(
            o => o.Id == request.OrganizationId,
            cancellationToken);

        if (!organizationExists)
        {
            throw new NotFoundException(
            nameof(Organization),
            request.OrganizationId);
        }

        var query = _context.OrganizationContacts
            .Include(m => m.Organization)
            .Include(m => m.OrganizationDepartment)
            .AsNoTracking()
            .OrderBy(m => m.LastName)
            .AsQueryable();

        if (request.OrganizationId.HasValue)
        {
            query = query.Where(m => m.OrganizationId == request.OrganizationId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var term = request.SearchTerm.Trim();

            query = query.Where(m =>
                EF.Functions.Like(m.LastName, $"%{term}%") ||
                EF.Functions.Like(m.Email, $"%{term}%"));
        }

        var paged = await PaginatedList<OrganizationContact>
            .CreateAsync(query, request.PageNumber, request.PageSize);

        var dtos = paged.Items
            .Select(OrganizationContactDto.FromEntity)
            .ToList();

        return new PaginatedList<OrganizationContactDto>(
            dtos,
            paged.TotalCount,
            paged.PageNumber,
            request.PageSize);
    }
}