using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Domain.Entities;

namespace POSShopTicketing.Infrastructure.Services;

/// <summary>
/// Default IAlertNotifier: logs every automated-workflow alert AND, when
/// it targets a specific TeamMember, writes it to their in-app
/// Notification feed. Swap the registration in DependencyInjection.cs
/// for a real push channel (email/Slack/SignalR/SMS) - nothing else in
/// the app needs to change.
/// </summary>
public class AlertNotifier : IAlertNotifier
{
    private readonly ILogger<AlertNotifier> _logger;
    private readonly IApplicationDbContext _context;
    private readonly IDateTime _dateTime;

    public AlertNotifier(ILogger<AlertNotifier> logger, IApplicationDbContext context, IDateTime dateTime)
    {
        _logger = logger;
        _context = context;
        _dateTime = dateTime;
    }

    public async Task NotifyAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ALERT [{Type}] {Title}: {Body} (TenantId={TenantId}, TicketId={TicketId}, TeamMemberId={TeamMemberId})",
            message.Type, message.Title, message.Body, message.TenantId, message.TicketId, message.TeamMemberId);

        if (message.TeamMemberId is null || message.TenantId is null)
        {
            return;
        }

        _context.Notifications.Add(new Notification
        {
            TenantId = message.TenantId.Value,
            TeamMemberId = message.TeamMemberId.Value,
            Type = message.Type,
            Title = message.Title,
            Message = message.Body,
            Body = message.Body,
            TicketId = message.TicketId,
            IsRead = false,
            CreatedAt = _dateTime.Now
        });

        await _context.SaveChangesAsync(cancellationToken);
    }
}
