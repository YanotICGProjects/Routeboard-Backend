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
    .Include(x => x.Mailbox)
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
            .Where(x => x.TicketId == ticket.Id)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var attachments = await _context.TicketAttachments
            .AsNoTracking()
            .Where(a => a.TicketId == ticket.Id)
            .ToListAsync(cancellationToken);

        return new TicketThreadDto
        {
            TicketId = ticket.Id,
            TicketNumber = ticket.TicketNumber,
            Subject = ticket.Subject,

            MailboxId = ticket.MailboxId,
            MailboxAddress = ticket.Mailbox?.EmailAddress,

            IsEmailTicket = ticket.MailboxId != null,

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

                    Attachments = attachments
                        .Where(a => a.TicketMessageId == x.Id)
                        .Select(a => new TicketAttachmentDto(
                            a.Id,
                            a.TicketMessageId,
                            a.FileName,
                            a.ContentType,
                            a.SizeBytes,
                            a.IsInline))
                        .ToList()
                })
                .ToList()
        };
    }
}