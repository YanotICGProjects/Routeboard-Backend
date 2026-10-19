using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Auth.Common;
using POSShopTicketing.Application.Auth.Events;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Auth.Commands.AcceptInvite;

public record AcceptInviteCommand : IRequest<AuthResultDto>
{
    public string InviteToken { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public class AcceptInviteCommandHandler : IRequestHandler<AcceptInviteCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IPasswordHasherService _passwordHasher;
    private readonly IRefreshTokenService _tokenService;
    private readonly IDateTime _dateTime;
    private readonly AuthResultFactory _authResultFactory;
    private readonly IPublisher _publisher;

    public AcceptInviteCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        IPasswordHasherService passwordHasher,
        IRefreshTokenService tokenService,
        IDateTime dateTime,
        AuthResultFactory authResultFactory,
        IPublisher publisher)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _dateTime = dateTime;
        _authResultFactory = authResultFactory;
        _publisher = publisher;
    }

    public async Task<AuthResultDto> Handle(AcceptInviteCommand request, CancellationToken cancellationToken)
    {
        var presentedHash = _tokenService.Hash(request.InviteToken);

        var teamMember = await _context.TeamMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                u => u.InviteTokenHash == presentedHash && u.Status == TeamMemberStatus.Invited,
                cancellationToken);

        if (teamMember is null || teamMember.InviteTokenExpiresAt is null || teamMember.InviteTokenExpiresAt < _dateTime.Now)
        {
            throw new UnauthorizedException("This invite link is invalid or has expired.");
        }

        // See ICurrentTenantService.SetTenant: this update targets a
        // real, already-tenant-scoped row and there's no JWT yet to
        // carry a tenant_id claim for this public accept-invite
        // endpoint - without this, RLS would reject the update below.
        _currentTenantService.SetTenant(teamMember.TenantId);

        teamMember.PasswordHash = _passwordHasher.Hash(request.Password);
        teamMember.Status = TeamMemberStatus.Active;
        teamMember.InviteTokenHash = null;
        teamMember.InviteTokenExpiresAt = null;
        teamMember.FirstName = request.FirstName;
        teamMember.LastName = request.LastName;


        await _context.SaveChangesAsync(cancellationToken);

        var tenant = await _context.Tenants
    .FirstOrDefaultAsync(t => t.Id == teamMember.TenantId, cancellationToken)
    ?? throw new UnauthorizedException("Tenant not found.");

        var result = await _authResultFactory.IssueAsync(teamMember, tenant, cancellationToken);

        await _publisher.Publish(
            new TeamMemberLoggedInEvent(teamMember.TenantId, teamMember.Id, teamMember.Email, teamMember.FullName, teamMember.Role.ToString()),
            cancellationToken);

        return result;
    }
}
