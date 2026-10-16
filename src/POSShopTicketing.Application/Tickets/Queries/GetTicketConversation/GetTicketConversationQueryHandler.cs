using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;

public class GetTicketConversationQueryHandler
    : IRequestHandler<GetTicketConversationQuery, List<TicketConversationDto>>
{
    private readonly IApplicationDbContext _context;

    public GetTicketConversationQueryHandler(
        IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<TicketConversationDto>> Handle(
        GetTicketConversationQuery request,
        CancellationToken cancellationToken)
    {
        return await _context.TicketMessages
            .AsNoTracking()
            .Include(x => x.Ticket)
            .Include(x => x.AuthorTeamMember)
            .Where(x => x.Ticket!.TicketNumber == request.TicketNumber)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new TicketConversationDto
            {
                Id = x.Id,
                CreatedAt = x.CreatedAt,
                Direction = x.Direction.ToString(),
                AuthorType = x.AuthorType.ToString(),
                AuthorName = x.AuthorType == MessageAuthorType.TeamMember
                    ? x.AuthorTeamMember!.FullName
                    : x.AuthorName,
                AuthorEmail = x.AuthorEmail,
                Body = x.Body,
                MessageId = x.MessageId,
                InReplyToMessageId = x.InReplyToMessageId
            })
            .ToListAsync(cancellationToken);
    }
}