using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ValidationException = POSShopTicketing.Application.Common.Exceptions.ValidationException;

namespace POSShopTicketing.Application.Auth.Commands.ResendInvite;

public sealed class ResendInviteCommandHandler : IRequestHandler<ResendInviteCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;
    private readonly IAppUrlProvider _appUrlProvider;
    private readonly IRefreshTokenService _tokenService;

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexString(bytes);
    }

    public ResendInviteCommandHandler(
        IApplicationDbContext context,
        IEmailSender emailSender,
        IRefreshTokenService tokenService,
        IAppUrlProvider appUrlProvider)
    {
        _context = context;
        _emailSender = emailSender;
        _tokenService = tokenService;
        _appUrlProvider = appUrlProvider;
    }

    public async Task Handle(
        ResendInviteCommand request,
        CancellationToken cancellationToken)
    {
        var member = await _context.TeamMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                x => x.Id == request.TeamMemberId,
                cancellationToken);

        if (member is null)
        {
            throw new NotFoundException(
                nameof(TeamMember),
                request.TeamMemberId);
        }

        if (member.Status == TeamMemberStatus.Active)
        {
            throw new ValidationException(
                new[]
                {
            new ValidationFailure(
                nameof(request.TeamMemberId),
                "User has already accepted the invitation.")
                });
        }

        var (plainTextToken, tokenHash, _) = _tokenService.GenerateToken();

        tokenHash = ComputeHash(plainTextToken);

        var expiresAt = DateTime.UtcNow.AddDays(7);

        member.InviteTokenHash = tokenHash;
        member.InviteTokenExpiresAt = expiresAt;

        await _context.SaveChangesAsync(cancellationToken);

        await SendInviteEmailAsync(
            member,
            plainTextToken,
            expiresAt,
            cancellationToken);
    }

    private async Task SendInviteEmailAsync(
        TeamMember member,
        string plainTextToken,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        var acceptUrl =
            _appUrlProvider.BuildInviteAcceptUrl(plainTextToken);

        var bodyHtml = string.IsNullOrEmpty(acceptUrl)
            ? $@"
<p>Hello,</p>

<p>Your previous invitation may have expired or been deleted.</p>

<p>A new invitation has been generated for you.</p>

<p>
Please contact your administrator because the invitation link is currently unavailable.
</p>

<p>
This invitation expires on
<strong>{expiresAt:yyyy-MM-dd HH:mm} UTC</strong>.
</p>"
            : $@"
<div style=""font-family:Arial,sans-serif;font-size:14px;line-height:1.6;color:#333333;"">

    <p>Hello,</p>

    <p>
        Your previous invitation may have expired or been deleted.
    </p>

    <p>
        A new invitation has been generated for you.
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
        If the button above does not work, copy and paste this link into your browser:
    </p>

    <p style=""word-break:break-all;"">
        <a href=""{acceptUrl}""
           target=""_blank""
           rel=""noopener noreferrer"">
            {WebUtility.HtmlEncode(acceptUrl)}
        </a>
    </p>

    <p>
        This invitation expires on
        <strong>{expiresAt:yyyy-MM-dd HH:mm} UTC</strong>.
    </p>

    <p>
        If this invitation expires before you accept it,
        please contact your administrator for a new invitation.
    </p>

</div>";

        await _emailSender.SendAsync(
            member.Email,
            member.FullName,
            "Your RouteBoard invitation has been resent",
            bodyHtml,
            cancellationToken);
    }
}