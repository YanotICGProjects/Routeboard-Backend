using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;

namespace POSShopTicketing.Application.Auth.Queries.CheckWorkspaceAlias;

public class CheckWorkspaceAliasQueryHandler
    : IRequestHandler<
        CheckWorkspaceAliasQuery,
        bool>
{
    private const string Domain =
        "support.routeboard.online";

    private readonly IApplicationDbContext _context;

    public CheckWorkspaceAliasQueryHandler(
        IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(
        CheckWorkspaceAliasQuery request,
        CancellationToken cancellationToken)
    {
        var alias =
            request.Alias
                .Trim()
                .ToLowerInvariant();

        var email =
            $"{alias}@{Domain}";

        var exists =
            await _context.Tenants
                .AsNoTracking()
                .AnyAsync(
                    x => x.SupportEmail == email,
                    cancellationToken);

        return !exists;
    }
}