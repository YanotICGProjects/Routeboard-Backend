using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Auth.Common;
using POSShopTicketing.Application.Auth.Events;
using POSShopTicketing.Application.Auth.Queries.RegisterTenantDto;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Domain.Exceptions;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace POSShopTicketing.Application.Auth.Commands.RegisterTenant;

/// <summary>
/// Self-serve onboarding: "Because other companies will eventually
/// register as Service Providers alongside yours" (spec, section 02).
/// Creates a brand-new Tenant and its first TeamMember as Owner, and
/// logs them straight in - the practical bootstrap path, since nothing
/// else in the system can create the first Owner of a new tenant.
/// </summary>
public record RegisterTenantCommand : IRequest<AuthResultDto>
{
    public string TenantName { get; init; } = string.Empty;
    public string OwnerEmail { get; init; } = string.Empty;
    public string OwnerPassword { get; init; } = string.Empty;
    public string OwnerFirstName { get; init; } = string.Empty;
    public string OwnerLastName { get; init; } = string.Empty;
    public string WorkspaceAlias { get; init; } = string.Empty;
    public string WorkspaceDomain { get; init; } = string.Empty;
    public string VerificationToken { get; init; } = string.Empty;
    public List<SignupInviteDto> Invites { get; init; } = new();
}

public class RegisterTenantCommandHandler : IRequestHandler<RegisterTenantCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly IEmailSender _emailSender;
    private readonly IPasswordHasherService _passwordHasher;
    private readonly AuthResultFactory _authResultFactory;
    private readonly IPublisher _publisher;
    private readonly IDateTime _dateTime;
    private readonly IAppUrlProvider _appUrlProvider;

    private readonly IRefreshTokenService _tokenService;

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes);
    }

    public RegisterTenantCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        IPasswordHasherService passwordHasher,
        AuthResultFactory authResultFactory,
        IPublisher publisher,
        IRefreshTokenService tokenService,
        IEmailSender emailSender,
        IAppUrlProvider appUrlProvider,
        IDateTime dateTime)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _passwordHasher = passwordHasher;
        _authResultFactory = authResultFactory;
        _publisher = publisher;
        _tokenService = tokenService;
        _appUrlProvider = appUrlProvider;
        _emailSender = emailSender;
        _dateTime = dateTime;
    }

    public async Task<AuthResultDto> Handle(RegisterTenantCommand request, CancellationToken cancellationToken)
    {
        var workspaceEmail = $"{request.WorkspaceAlias}@support.routeboard.online";

        var exists = await _context.Tenants
        .AnyAsync(
            x => x.SupportEmail == workspaceEmail,
            cancellationToken);

        if (exists)
        {
            throw new DomainException(
                "Support Workspace alias already exists.");
        }

        var normalizedEmail = request.OwnerEmail.Trim().ToLowerInvariant();

        var emailTaken = await _context.TeamMembers
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalizedEmail, cancellationToken);

        if (emailTaken)
        {
            throw new DomainException($"An account with email \"{normalizedEmail}\" already exists.");
        }

        //var slug = await GenerateUniqueSlugAsync(request.TenantName, cancellationToken);

        var verified = await _context.EmailVerificationOtps
        .AsNoTracking()
        .AnyAsync(
            x =>
                x.Email == normalizedEmail &&
                x.VerificationToken ==
                    request.VerificationToken &&
                x.IsUsed,
            cancellationToken);

        if (!verified)
        {
            throw new DomainException(
                "Email must be verified.");
        }


        var tenant = new Tenant
        {
            Name = request.TenantName.Trim(),
            Slug = request.WorkspaceAlias,
            TicketPrefix = BuildTicketPrefix(request.WorkspaceAlias),
            Plan = TenantPlan.Trial,
            Status = TenantStatus.Active,
            WorkspaceAlias = request.WorkspaceAlias,
            SupportEmail = workspaceEmail,
            WorkspaceDomain = request.WorkspaceDomain
        };


    //    var slugExists = await _context.Tenants
    //.AnyAsync(x => x.Slug == tenant.Slug, cancellationToken);

    //    if (slugExists)
    //    {
    //        throw new DomainException(
    //            $"A tenant with slug '{tenant.Slug}' already exists.");
    //    }

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(cancellationToken);

        // See ICurrentTenantService.SetTenant: the owner TeamMember row
        // below is RLS-protected, and there's no JWT yet to carry a
        // tenant_id claim for this public bootstrap endpoint - the
        // tenant we just created is the one to scope that write to.
        _currentTenantService.SetTenant(tenant.Id);

      
        var expiresAt = _dateTime.Now.AddDays(7);

        foreach (var invite in request.Invites)
        {
            if (string.IsNullOrWhiteSpace(invite.Email) || !invite.Role.HasValue)
            {
                continue;
            }

            var inviteEmails = request.Invites
    .Where(x => !string.IsNullOrWhiteSpace(x.Email))
    .Select(x => x.Email!.Trim().ToLowerInvariant())
    .ToList();

            var existingEmails = await _context.TeamMembers
                .AsNoTracking()
                .Where(x => inviteEmails.Contains(x.Email))
                .Select(x => x.Email)
                .ToListAsync(cancellationToken);

            if (existingEmails.Any())
            {
                throw new DomainException(
                    $"The following email addresses already exist: {string.Join(", ", existingEmails)}");
            }

            var inviteEmail = invite.Email.Trim().ToLowerInvariant();

            if (inviteEmail == normalizedEmail)
            {
                throw new DomainException(
                "Invite email address cannot be the same as the owner email.");
            }

            var (plainTextToken, tokenHash, _) = _tokenService.GenerateToken();

           
                
                tokenHash = ComputeHash(plainTextToken);
            
            

            var teamMemberInvite = new TeamMember
            {
                TenantId = tenant.Id,
                Email = invite.Email.Trim().ToLowerInvariant(),
                Role = invite.Role.Value,
                Status = TeamMemberStatus.Invited,
                InviteTokenHash = tokenHash,
                InviteTokenExpiresAt = expiresAt
            };

            _context.TeamMembers.Add(teamMemberInvite);

            await SendInviteEmailAsync(
                teamMemberInvite,
                tenant.Name,
                plainTextToken,
                expiresAt,
                cancellationToken);
        }


        await _context.SaveChangesAsync(cancellationToken);

        var owner = new TeamMember
        {
            TenantId = tenant.Id,
            Email = normalizedEmail,
            PasswordHash = _passwordHasher.Hash(request.OwnerPassword),
            FirstName = request.OwnerFirstName.Trim(),
            LastName = request.OwnerLastName.Trim(),
            Role = TeamMemberRole.Owner,
            Status = TeamMemberStatus.Active
        };

        

        _context.TeamMembers.Add(owner);
        await _context.SaveChangesAsync(cancellationToken);

        var result = await _authResultFactory.IssueAsync(owner, cancellationToken);

        await _publisher.Publish(
            new TeamMemberLoggedInEvent(tenant.Id, owner.Id, owner.Email, owner.FullName, owner.Role.ToString()),
            cancellationToken);

        

        return result;
    }

    public string Hash(string plainTextToken)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plainTextToken));
        return Convert.ToHexString(bytes);
    }

    private async Task SendInviteEmailAsync(
        TeamMember teamMember, string tenantName, string plainTextToken, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var acceptUrl = _appUrlProvider.BuildInviteAcceptUrl(plainTextToken);
        var inviterLabel = _currentUserService.Email ?? "a team admin";
        var encodedTenantName = WebUtility.HtmlEncode(tenantName);
        var encodedToken = WebUtility.HtmlEncode(plainTextToken);

        var bodyHtml = string.IsNullOrEmpty(acceptUrl)
    ? $@"
<p>{WebUtility.HtmlEncode(inviterLabel)} invited you to join <strong>{encodedTenantName}</strong> on RouteBoard as <strong>{teamMember.Role}</strong>.</p>
<p>Unfortunately, the invitation link is currently unavailable.</p>
<p>Please contact your administrator for a new invitation.</p>
<p>This invitation expires <strong>{expiresAt:yyyy-MM-dd HH:mm} UTC</strong>.</p>"
    : $@"
<div style=""font-family:Arial,sans-serif;font-size:14px;line-height:1.6;color:#333333;"">

    <p>
        {WebUtility.HtmlEncode(inviterLabel)} invited you to join
        <strong>{encodedTenantName}</strong>
        on RouteBoard as
        <strong>{teamMember.Role}</strong>.
    </p>

    <p>
        Click the button below to accept your invitation and complete your account setup.
    </p>

    <p style=""margin:30px 0;"">
        <a href=""{acceptUrl}""
           target=""_blank""
           style=""
               background-color:#2563eb;
               color:#ffffff;
               padding:12px 24px;
               text-decoration:none;
               border-radius:6px;
               display:inline-block;
               font-weight:bold;
               font-family:Arial,sans-serif;
           "">
            Accept Invitation
        </a>
    </p>

    <p>
        If the button does not work, use the invitation link below:
    </p>

    <p style=""word-break:break-all;margin:10px 0;"">
        <a href=""{acceptUrl}""
           target=""_blank""
           rel=""noopener noreferrer""
           style=""color:#2563eb;text-decoration:underline;"">
            {acceptUrl}
        </a>
    </p>

    <p>
        This invitation expires
        <strong>{expiresAt:yyyy-MM-dd HH:mm} UTC</strong>.
    </p>

    <p>
        If this invitation expires before you accept it, please contact your administrator to request a new invitation.
    </p>

    <p style=""color:#666666;font-size:12px;"">
        If you were not expecting this invitation, you can safely ignore this email.
    </p>

</div>";

        await _emailSender.SendAsync(
            teamMember.Email, teamMember.FullName,
            $"You're invited to join {tenantName} on RouteBoard", bodyHtml, cancellationToken);
    }

    //private async Task<string> GenerateUniqueSlugAsync(string tenantName, CancellationToken cancellationToken)
    //{
    //    var baseSlug = Regex.Replace(tenantName.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    //    if (string.IsNullOrEmpty(baseSlug))
    //    {
    //        baseSlug = "tenant";
    //    }

    //    var slug = baseSlug;
    //    var suffix = 1;

    //    while (await _context.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug, cancellationToken))
    //    {
    //        suffix++;
    //        slug = $"{baseSlug}-{suffix}";
    //    }

    //    return slug;
    //}

    private static string BuildTicketPrefix(string slug)
    {
        var letters = Regex.Replace(slug, "[^a-zA-Z]", "").ToUpperInvariant();
        return string.IsNullOrEmpty(letters) ? "TKT" : letters[..Math.Min(5, letters.Length)];
    }
}
