using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;

namespace POSShopTicketing.Application.Auth.Events;

public class TeamMemberLoggedInEventHandler
    : INotificationHandler<TeamMemberLoggedInEvent>
{
    private readonly IAuditTrailService _auditTrailService;
    private readonly IApplicationDbContext _context;
    private readonly IEmailSender _emailSender;

    public TeamMemberLoggedInEventHandler(
        IAuditTrailService auditTrailService,
        IApplicationDbContext context,
        IEmailSender emailSender)
    {
        _auditTrailService = auditTrailService;
        _context = context;
        _emailSender = emailSender;
    }

    public async Task Handle(
        TeamMemberLoggedInEvent notification,
        CancellationToken cancellationToken)
    {
        await _auditTrailService.RecordAsync(
            new AuditTrailEntry
            {
                TenantId = notification.TenantId,
                ActorTeamMemberId = notification.TeamMemberId,
                ActorDisplayName = notification.FullName,
                Action = "TeamMember.LoggedIn",
                EntityType = nameof(Domain.Entities.TeamMember),
                EntityId = notification.TeamMemberId.ToString(),
                Summary =
                    $"{notification.FullName} ({notification.Email}, {notification.Role}) logged in."
            },
            cancellationToken);

        await _emailSender.SendAsync(
            notification.Email,
            notification.FullName,
            "RouteBoard Login Alert",
            $@"
            <div style='font-family:Arial,sans-serif;font-size:14px'>
                <p>Hello {notification.FullName},</p>

                <p>
                    Your RouteBoard account was successfully signed in.
                </p>

                <p>
                    <strong>Role:</strong>
                    {notification.Role}
                </p>

                <p>
                    If this login was not performed by you,
                    please change your password immediately.
                </p>

                <p>
                    Thank you.
                </p>
            </div>",
            cancellationToken);
    }
}