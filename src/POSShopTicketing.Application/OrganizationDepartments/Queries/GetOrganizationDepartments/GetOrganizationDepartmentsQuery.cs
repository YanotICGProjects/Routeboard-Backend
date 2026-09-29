using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Application.OrganizationTeams.Queries.GetOrganizationTeams;

public record GetOrganizationDepartmentsQuery(
    Guid OrganizationId,
    int PageNumber = 1,
    int PageSize = 20)
    : IRequest<PaginatedResponse<OrganizationDepartmentDto>>;

public class GetOrganizationTeamsQueryHandler
    : IRequestHandler<
        GetOrganizationDepartmentsQuery,
        PaginatedResponse<OrganizationDepartmentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetOrganizationTeamsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PaginatedResponse<OrganizationDepartmentDto>> Handle(
     GetOrganizationDepartmentsQuery request,
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

        var query = _context.OrganizationDepartments
            .Include(d => d.Contacts)
            .AsNoTracking()
            .Where(d => d.OrganizationId == request.OrganizationId);

        var totalCount = await query.CountAsync(cancellationToken);

        var departments = await query
            .OrderBy(d => d.Name)
            .Skip((request.PageNumber - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var result = departments
            .Select(OrganizationDepartmentDto.FromEntity)
            .ToList();

        return PaginatedResponse<OrganizationDepartmentDto>.Create(
            result,
            request.PageNumber,
            request.PageSize,
            totalCount);
    }

}