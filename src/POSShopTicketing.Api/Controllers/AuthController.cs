using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POSShopTicketing.Application.Auth.Commands.AcceptInvite;
using POSShopTicketing.Application.Auth.Commands.InviteTeamMember;
using POSShopTicketing.Application.Auth.Commands.Login;
using POSShopTicketing.Application.Auth.Commands.Logout;
using POSShopTicketing.Application.Auth.Commands.RefreshToken;
using POSShopTicketing.Application.Auth.Commands.RegisterTenant;
using POSShopTicketing.Application.Auth.Commands.ResendInvite;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers;

/// <summary>
/// POST /auth/login, /auth/refresh, /auth/invite/accept - and, beyond
/// the spec's literal three, /auth/register-tenant (the self-serve
/// bootstrap every other tenant needs to come from) and /auth/invite
/// (Owner/Admin only, the other half of the invite/accept pair).
/// </summary>
public class AuthController : ApiControllerBase
{
    [AllowAnonymous]
    [HttpPost("register-tenant")]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> RegisterTenant(RegisterTenantCommand command)
    {
        var result = await Mediator.Send(command);
        return Ok(ApiResponse<AuthResultDto>.Success(result, "Tenant created."));
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Login(LoginCommand command)
    {
        var result = await Mediator.Send(command);
        return Ok(ApiResponse<AuthResultDto>.Success(result, "Logged in."));
    }

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> Refresh(RefreshTokenCommand command)
    {
        var result = await Mediator.Send(command);
        return Ok(ApiResponse<AuthResultDto>.Success(result, "Token refreshed."));
    }

    /// <summary>Revokes the given refresh token - records a
    /// TeamMember.LoggedOut audit entry. Anonymous rather than requiring
    /// a still-valid access token: by the time someone wants to log out,
    /// a short-lived access token may well have already expired, and the
    /// refresh token itself is what actually needs revoking.</summary>
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<ActionResult<ApiResponse<object>>> Logout(LogoutCommand command)
    {
        await Mediator.Send(command);
        return Ok(ApiResponse<object>.Success(new { }, "Logged out."));
    }

    /// <summary>Owner/Admin only - "Admin: Manage team members & roles".</summary>
    [Authorize(Roles = "Owner,Admin")]
    [HttpPost("invite")]
    public async Task<ActionResult<ApiResponse<InviteTeamMemberResult>>> Invite(InviteTeamMemberCommand command)
    {
        var result = await Mediator.Send(command);
        return Ok(ApiResponse<InviteTeamMemberResult>.Success(result, "Invitation sent."));
    }

    [HttpPost("invite/{teamMemberId:guid}/resend")]
    public async Task<IActionResult> ResendInvite(Guid teamMemberId)
    {
        await Mediator.Send(new ResendInviteCommand(teamMemberId));

        return Ok(new
        {
            succeeded = true,
            message = "Invitation resent successfully."
        });
    }

    [AllowAnonymous]
    [HttpPost("invite/accept")]
    public async Task<ActionResult<ApiResponse<AuthResultDto>>> AcceptInvite(AcceptInviteCommand command)
    {
        var result = await Mediator.Send(command);
        return Ok(ApiResponse<AuthResultDto>.Success(result, "Invitation accepted."));
    }

}
