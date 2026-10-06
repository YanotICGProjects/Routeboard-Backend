using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;

namespace POSShopTicketing.Application.Notifications.Commands.MarkNotificationAsRead;

public record MarkNotificationAsReadCommand(Guid NotificationId)
    : IRequest;

public class MarkNotificationAsReadCommandHandler
    : IRequestHandler<MarkNotificationAsReadCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<MarkNotificationAsReadCommand> _logger;

    public MarkNotificationAsReadCommandHandler(
        IApplicationDbContext context, ICurrentUserService currentUserService, ILogger<MarkNotificationAsReadCommand> logger)
    {
        _logger = logger;
        _context = context;
        _currentUserService = currentUserService;
    }

    public async Task Handle(
        MarkNotificationAsReadCommand request,
        CancellationToken cancellationToken)
    {
        var notification = await _context.Notifications
    .FirstOrDefaultAsync(
        x => x.Id == request.NotificationId &&
             x.TeamMemberId == _currentUserService.TeamMemberId,
        cancellationToken);

        if (notification == null)
        {
            throw new NotFoundException(
                nameof(notification),
                request.NotificationId);
        }

        notification.IsRead = true;

        notification.ReadAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
    "Event Email: {EventEmail}, CurrentUser Email: {CurrentUserEmail}",
    notification.Email,
    _currentUserService.Email);
    }
}