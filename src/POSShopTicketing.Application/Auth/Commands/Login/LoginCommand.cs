using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Auth.Common;
using POSShopTicketing.Application.Auth.Events;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Auth.Commands.Login;

public record LoginCommand : IRequest<AuthResultDto>
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}

public class LoginCommandHandler : IRequestHandler<LoginCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _context;
    private readonly IPasswordHasherService _passwordHasher;
    private readonly AuthResultFactory _authResultFactory;
    private readonly IPublisher _publisher;

    public LoginCommandHandler(
        IApplicationDbContext context,
        IPasswordHasherService passwordHasher,
        AuthResultFactory authResultFactory,
        IPublisher publisher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _authResultFactory = authResultFactory;
        _publisher = publisher;
    }

    public async Task<AuthResultDto> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var teamMember = await _context.TeamMembers
            .IgnoreQueryFilters() // login runs before a tenant is known
            .FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (teamMember is null
            || teamMember.Status != TeamMemberStatus.Active
            || !_passwordHasher.Verify(teamMember.PasswordHash, request.Password))
        {
            throw new UnauthorizedException("Invalid email or password.");
        }

        var tenant = await _context.Tenants
    .FirstOrDefaultAsync(t => t.Id == teamMember.TenantId, cancellationToken)
    ?? throw new UnauthorizedException("Tenant not found.");

        var result = await _authResultFactory.IssueAsync(teamMember, tenant, cancellationToken);

        await _publisher.Publish(
            new TeamMemberLoggedInEvent(result.TenantId, teamMember.Id, teamMember.Email, teamMember.FullName, teamMember.Role.ToString()),
            cancellationToken);

        return result;
    }
}
