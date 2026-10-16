using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Queries.GetTicketAttachment;
using POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Queries.GetTicketThread;

public class GetTicketThreadQueryHandler
    : IRequestHandler<GetTicketThreadQuery, TicketThreadDto>
{
    private readonly IApplicationDbContext _context;

    public GetTicketThreadQueryHandler(
        IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<TicketThreadDto> Handle(
        GetTicketThreadQuery request,
        CancellationToken cancellationToken)
    {
        var ticket = await _context.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.TicketNumber == request.TicketNumber,
                cancellationToken);

        if (ticket == null)
        {
            throw new NotFoundException(
                nameof(Ticket),
                request.TicketNumber);
        }

        var messages = await _context.TicketMessages
            .AsNoTracking()
            .Include(x => x.AuthorTeamMember)
            .Include(x => x.Attachments)
            .Where(x => x.TicketId == ticket.Id)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        return new TicketThreadDto
        {
            TicketId = ticket.Id,
            TicketNumber = ticket.TicketNumber,
            Subject = ticket.Subject,

            Messages = messages.Select(x =>
                new TicketConversationDto
                {
                    Id = x.Id,
                    CreatedAt = x.CreatedAt,

                    Direction = x.Direction.ToString(),

                    AuthorType = x.AuthorType.ToString(),

                    AuthorName =
                        x.AuthorType == MessageAuthorType.TeamMember
                        ? x.AuthorTeamMember != null
                            ? $"{x.AuthorTeamMember.FirstName} {x.AuthorTeamMember.LastName}"
                            : null
                        : x.AuthorName,

                    AuthorEmail =
                        x.AuthorType == MessageAuthorType.TeamMember
                        ? x.AuthorTeamMember?.Email
                        : x.AuthorEmail,

                    Body = x.Body,

                    MessageId = x.MessageId,

                    InReplyToMessageId = x.InReplyToMessageId,

                    Attachments = x.Attachments
                        .Select(a => new TicketAttachmentDto
                        {
                            Id = a.Id,
                            FileName = a.FileName,
                            ContentType = a.ContentType,
                            SizeBytes = a.SizeBytes
                        })
                        .ToList()
                })
                .ToList()
        };
    }
}