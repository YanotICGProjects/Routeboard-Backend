using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Auth.Common;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Auth.Commands.RefreshToken;

/// <summary>
/// Rotates a refresh token: the presented token is revoked and replaced
/// by a brand-new one in the same call, so a token can only ever be
/// redeemed once. If a caller ever presents a token that's already been
/// rotated out, every other active token on that TeamMember is revoked
/// too - the standard signal that a refresh token was stolen and reused.
/// </summary>
public record RefreshTokenCommand(string RefreshToken) : IRequest<AuthResultDto>;

public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IDateTime _dateTime;
    private readonly AuthResultFactory _authResultFactory;

    public RefreshTokenCommandHandler(
        IApplicationDbContext context,
        IRefreshTokenService refreshTokenService,
        IDateTime dateTime,
        AuthResultFactory authResultFactory)
    {
        _context = context;
        _refreshTokenService = refreshTokenService;
        _dateTime = dateTime;
        _authResultFactory = authResultFactory;
    }

    public async Task<AuthResultDto> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var presentedHash = _refreshTokenService.Hash(request.RefreshToken);

        var token = await _context.RefreshTokens
            .IgnoreQueryFilters()
            .Include(t => t.TeamMember)
            .FirstOrDefaultAsync(t => t.TokenHash == presentedHash, cancellationToken);

        if (token is null)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        if (!token.IsActive)
        {
            // Already used/expired/revoked - if it had been rotated
            // (ReplacedByTokenHash set), this looks like token theft:
            // revoke every other active token for this TeamMember too.
            if (token.ReplacedByTokenHash is not null)
            {
                var siblings = await _context.RefreshTokens
                    .IgnoreQueryFilters()
                    .Where(t => t.TeamMemberId == token.TeamMemberId && t.RevokedAt == null)
                    .ToListAsync(cancellationToken);

                foreach (var sibling in siblings)
                {
                    sibling.RevokedAt = _dateTime.Now;
                }

                await _context.SaveChangesAsync(cancellationToken);
            }

            throw new UnauthorizedException("Refresh token is no longer valid. Please log in again.");
        }

        var teamMember = token.TeamMember
            ?? throw new UnauthorizedException("Refresh token is no longer valid. Please log in again.");

        if (teamMember.Status != TeamMemberStatus.Active)
        {
            throw new UnauthorizedException("This account is no longer active.");
        }

        var (newPlainTextToken, newHash, newExpiresAt) = _refreshTokenService.GenerateToken();

        token.RevokedAt = _dateTime.Now;
        token.ReplacedByTokenHash = newHash;

        _context.RefreshTokens.Add(new RefreshTokenz
        {
            TeamMemberId = teamMember.Id,
            TokenHash = newHash,
            CreatedAt = _dateTime.Now,
            ExpiresAt = newExpiresAt
        });

        await _context.SaveChangesAsync(cancellationToken);

        // AuthResultFactory would issue yet another new refresh token on
        // top of the one just minted above - instead build the result
        // directly here so exactly one new refresh token comes back.
        var tenant = await _context.Tenants
    .FirstOrDefaultAsync(t => t.Id == teamMember.TenantId, cancellationToken)
    ?? throw new UnauthorizedException("Tenant not found.");

        return await _authResultFactory.IssueAccessTokenOnlyAsync(
            teamMember,
            tenant,
            newPlainTextToken,
            newExpiresAt);
    }
}
