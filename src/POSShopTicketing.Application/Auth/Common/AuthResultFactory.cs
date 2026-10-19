using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Application.Auth.Common;

/// <summary>
/// Shared "issue access + refresh token pair for this TeamMember" logic
/// used by RegisterTenant, Login, RefreshToken, and AcceptInvite - so
/// all four hand back the exact same AuthResultDto shape the same way.
/// </summary>
public class AuthResultFactory
{
    private readonly IApplicationDbContext _context;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IDateTime _dateTime;

    public AuthResultFactory(
        IApplicationDbContext context,
        IJwtTokenGenerator jwtTokenGenerator,
        IRefreshTokenService refreshTokenService,
        IDateTime dateTime)
    {
        _context = context;
        _jwtTokenGenerator = jwtTokenGenerator;
        _refreshTokenService = refreshTokenService;
        _dateTime = dateTime;
    }

    public async Task<AuthResultDto> IssueAsync(TeamMember teamMember, Tenant tenant, CancellationToken cancellationToken)
    {
        var (accessToken, accessTokenExpiresAt) = _jwtTokenGenerator.GenerateAccessToken(teamMember);
        var (plainTextRefreshToken, refreshTokenHash, refreshTokenExpiresAt) = _refreshTokenService.GenerateToken();

        _context.RefreshTokens.Add(new RefreshTokenz
        {
            TeamMemberId = teamMember.Id,
            TokenHash = refreshTokenHash,
            CreatedAt = _dateTime.Now,
            ExpiresAt = refreshTokenExpiresAt
        });

        await _context.SaveChangesAsync(cancellationToken);

        return BuildDto(teamMember, tenant, accessToken, accessTokenExpiresAt, plainTextRefreshToken, refreshTokenExpiresAt);
    }

    /// <summary>Used only by RefreshTokenCommandHandler, which has
    /// already minted and persisted the new refresh token itself (as
    /// part of the same rotation transaction) - this just mints the new
    /// access token and assembles the response DTO around both, without
    /// creating a second refresh token.</summary>
    public Task<AuthResultDto> IssueAccessTokenOnlyAsync(
        TeamMember teamMember, Tenant tenant, string newPlainTextRefreshToken, DateTime refreshTokenExpiresAt)
    {
        var (accessToken, accessTokenExpiresAt) = _jwtTokenGenerator.GenerateAccessToken(teamMember);

        return Task.FromResult(
            BuildDto(teamMember, tenant, accessToken, accessTokenExpiresAt, newPlainTextRefreshToken, refreshTokenExpiresAt));
    }

    private static AuthResultDto BuildDto(
        TeamMember teamMember,
        Tenant tenant,
        string accessToken,
        DateTime accessTokenExpiresAt,
        string refreshToken,
        DateTime refreshTokenExpiresAt) => new()
    {
        TeamMemberId = teamMember.Id,
        TenantId = teamMember.TenantId == Guid.Empty ? null : teamMember.TenantId,
        Email = teamMember.Email,
        FirstName = teamMember.FirstName,
        LastName = teamMember.LastName,
        Role = teamMember.Role,
        WorkSpaceName = tenant.WorkSpaceName,
        SupportEmail = tenant.SupportEmail,
        AccessToken = accessToken,
        AccessTokenExpiresAt = accessTokenExpiresAt,
        RefreshToken = refreshToken,
        RefreshTokenExpiresAt = refreshTokenExpiresAt
    };
}
