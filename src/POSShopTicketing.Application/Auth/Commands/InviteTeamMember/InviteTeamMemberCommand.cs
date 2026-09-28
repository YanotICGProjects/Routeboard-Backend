using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Domain.Exceptions;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace POSShopTicketing.Application.Auth.Commands.InviteTeamMember;

/// <summary>
/// Owner/Admin only (enforced by [Authorize(Roles=...)] on the
/// controller). Creates an Invited TeamMember and a single-use token for
/// POST /auth/invite/accept, then emails that token directly to the
/// invitee via IEmailSender - it never appears in the API response,
/// which only the inviter sees. If the inviter (rather than the
/// invitee) held the secret that lets someone set a password, they
/// could set it themselves before the real person even knew an account
/// existed - the same reason Slack/GitHub/Notion invite links only ever
/// go to the invitee's own inbox.
/// </summary>
public record InviteTeamMemberCommand : IRequest<InviteTeamMemberResult>
{
    public string Email { get; init; } = string.Empty;
    //public string FirstName { get; init; } = string.Empty;
    //public string LastName { get; init; } = string.Empty;
    public TeamMemberRole Role { get; init; } = TeamMemberRole.Agent;
}

/// <summary>Deliberately does NOT include the invite token - see the
/// handler summary above. ExpiresAt lets the caller show "invitation
/// sent, expires on {date}" without ever holding the secret itself.</summary>
public record InviteTeamMemberResult(Guid TeamMemberId, DateTime ExpiresAt);

public class InviteTeamMemberCommandHandler : IRequestHandler<InviteTeamMemberCommand, InviteTeamMemberResult>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IRefreshTokenService _tokenService; // reused purely for its opaque-token generator
    private readonly IEmailSender _emailSender;
    private readonly IAppUrlProvider _appUrlProvider;
    private readonly IDateTime _dateTime;
    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes);
    }

    public InviteTeamMemberCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IRefreshTokenService tokenService,
        IEmailSender emailSender,
        IAppUrlProvider appUrlProvider,
        IDateTime dateTime)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _tokenService = tokenService;
        _emailSender = emailSender;
        _appUrlProvider = appUrlProvider;
        _dateTime = dateTime;
    }

    public async Task<InviteTeamMemberResult> Handle(InviteTeamMemberCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenantService.TenantId
            ?? throw new ForbiddenException("Invitations must be sent from within a tenant.");

        if (request.Role == TeamMemberRole.PlatformSuperAdmin)
        {
            throw new ForbiddenException("PlatformSuperAdmin cannot be granted through an invite.");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var emailTaken = await _context.TeamMembers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (emailTaken)
        {
            throw new DomainException($"An account with email \"{normalizedEmail}\" already exists.");
        }

        var tenant = await _context.Tenants.FindAsync(new object[] { tenantId }, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Tenant), tenantId);

        var (plainTextToken, tokenHash, _) = _tokenService.GenerateToken();

        
        tokenHash = ComputeHash(plainTextToken);

        var expiresAt = _dateTime.Now.AddDays(7);

        var teamMember = new TeamMember
        {
            TenantId = tenantId,
            Email = normalizedEmail,
            PasswordHash = string.Empty, // set on accept
            //FirstName = request.FirstName.Trim(),
            //LastName = request.LastName.Trim(),
            Role = request.Role,
            Status = TeamMemberStatus.Invited,
            InviteTokenHash = tokenHash,
            InviteTokenExpiresAt = expiresAt
        };

        _context.TeamMembers.Add(teamMember);
        await _context.SaveChangesAsync(cancellationToken);

        await SendInviteEmailAsync(teamMember, tenant.Name, plainTextToken, expiresAt, cancellationToken);

        return new InviteTeamMemberResult(teamMember.Id,  expiresAt);
    }

    private async Task SendInviteEmailAsync(
        TeamMember teamMember, string tenantName, string plainTextToken, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var acceptUrl = _appUrlProvider.BuildInviteAcceptUrl(plainTextToken);
        var inviterLabel = _currentUserService.Email ?? "a team admin";
        var encodedTenantName = WebUtility.HtmlEncode(tenantName);
        var encodedToken = WebUtility.HtmlEncode(plainTextToken);

        var bodyHtml = string.IsNullOrEmpty(acceptUrl)
            ? $"<p>{WebUtility.HtmlEncode(inviterLabel)} invited you to join <strong>{encodedTenantName}</strong> " +
              $"on POSShopTicketing as {teamMember.Role}.</p>" +
              $"<p>To accept, call <code>POST /api/auth/invite/accept</code> with this token:</p>" +
              $"<p style=\"font-family:monospace;background:#f4f4f4;padding:8px;word-break:break-all\">{encodedToken}</p>" +
              $"<p>This invite expires {expiresAt:yyyy-MM-dd HH:mm} UTC.</p>"
            : $"<p>{WebUtility.HtmlEncode(inviterLabel)} invited you to join <strong>{encodedTenantName}</strong> " +
              $"on POSShopTicketing as {teamMember.Role}.</p>" +
              $"<p><a href=\"{acceptUrl}\">Click here to accept your invitation</a></p>" +
              $"<p>Or use this token directly with <code>POST /api/auth/invite/accept</code>: " +
              $"<span style=\"font-family:monospace\">{encodedToken}</span></p>" +
              $"<p>This invite expires {expiresAt:yyyy-MM-dd HH:mm} UTC.</p>";

        await _emailSender.SendAsync(
            teamMember.Email, teamMember.FullName,
            $"You're invited to join {tenantName} on POSShopTicketing", bodyHtml, cancellationToken);
    }
}
