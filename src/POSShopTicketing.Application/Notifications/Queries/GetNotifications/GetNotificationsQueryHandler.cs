using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;

namespace POSShopTicketing.Application.Notifications.Queries.GetNotifications;

public class GetNotificationsQueryHandler
    : IRequestHandler<GetNotificationsQuery, List<NotificationDto>>
{
    private readonly IApplicationDbContext _context;

    private readonly ICurrentUserService _currentUserService;

    public GetNotificationsQueryHandler(
        IApplicationDbContext context,
        ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    public async Task<List<NotificationDto>> Handle(
        GetNotificationsQuery request,
        CancellationToken cancellationToken)
    {
        return await _context.Notifications
            .Where(x => x.TeamMemberId == _currentUserService.TeamMemberId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new NotificationDto
            {
                Id = x.Id,
                TicketId = x.TicketId,
                Title = x.Title,
                Message = !string.IsNullOrWhiteSpace(x.Message)
        ? x.Message
        : x.Body,
                IsRead = x.IsRead,
                CreatedAt = x.CreatedAt,
                ReadAt = x.ReadAt
            })
            .ToListAsync(cancellationToken);
    }
}