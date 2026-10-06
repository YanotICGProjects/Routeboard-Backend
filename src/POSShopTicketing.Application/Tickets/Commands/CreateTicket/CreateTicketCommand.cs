using MediatR;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Exceptions;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Events;
using POSShopTicketing.Domain.Entities;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Commands.CreateTicket;

/// <summary>
/// POST /tickets - manual creation, usable end to end (thread, status,
/// assignment) before email is ever wired up, per the spec's phased
/// roadmap (Phase 2 doesn't depend on Phase 3).
/// </summary>
public record CreateTicketCommand : IRequest<Guid>
{
    public string Subject { get; init; } = string.Empty;
    public string InitialMessageBody { get; init; } = string.Empty;
    public Guid? OrganizationId { get; init; }
    public Guid? OrganizationDepartmentId { get; init; }
    public Guid? OrganizationContactId { get; init; }
    public bool Escalated { get; init; }
    public TicketStatus Status { get; init; }
    public TicketPriority? Priority { get; init; }

    public Guid? AssignedToTeamMemberId { get; init; }
}

public class CreateTicketCommandHandler : IRequestHandler<CreateTicketCommand, Guid>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentTenantService _currentTenantService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ITicketNumberGenerator _ticketNumberGenerator;
    private readonly ITicketPriorityClassifier _priorityClassifier;
    private readonly ITicketAssignmentService _assignmentService;
    private readonly ISlaCalculator _slaCalculator;
    private readonly IHtmlSanitizerService _htmlSanitizer;
    private readonly IDateTime _dateTime;
    private readonly IPublisher _publisher;

    public CreateTicketCommandHandler(
        IApplicationDbContext context,
        ICurrentTenantService currentTenantService,
        ICurrentUserService currentUserService,
        ITicketNumberGenerator ticketNumberGenerator,
        ITicketPriorityClassifier priorityClassifier,
        ITicketAssignmentService assignmentService,
        ISlaCalculator slaCalculator,
        IHtmlSanitizerService htmlSanitizer,
        IDateTime dateTime,
        IPublisher publisher)
    {
        _context = context;
        _currentTenantService = currentTenantService;
        _currentUserService = currentUserService;
        _ticketNumberGenerator = ticketNumberGenerator;
        _priorityClassifier = priorityClassifier;
        _assignmentService = assignmentService;
        _slaCalculator = slaCalculator;
        _htmlSanitizer = htmlSanitizer;
        _dateTime = dateTime;
        _publisher = publisher;
    }

    public async Task<Guid> Handle(CreateTicketCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenantService.TenantId
            ?? throw new ForbiddenException("Tickets must be created from within a tenant.");

        var tenant = await _context.Tenants.FindAsync(new object[] { tenantId }, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        OrganizationDepartment? department = null;

        OrganizationContact? contact = null;
        if (request.OrganizationContactId.HasValue)
        {
            contact = await _context.OrganizationContacts.FindAsync(new object[] { request.OrganizationContactId.Value }, cancellationToken)
                ?? throw new NotFoundException(nameof(OrganizationContact), request.OrganizationContactId.Value);
        }

        var now = _dateTime.Now;
        var priority = request.Priority ?? _priorityClassifier.Classify(request.Subject, request.InitialMessageBody);
        var dueAt = await _slaCalculator.CalculateDueDateAsync(tenantId, priority, now, cancellationToken);

        var assignedToTeamMemberId = await _assignmentService.ResolveAssigneeAsync(
            tenantId, request.OrganizationId, request.OrganizationDepartmentId, request.OrganizationContactId, cancellationToken);

        var teamMember = await _context.TeamMembers
            .IgnoreQueryFilters() // login runs before a tenant is known
            .FirstOrDefaultAsync(u => u.Email == _currentUserService.Email.ToString(), cancellationToken);

        var ticket = new Ticket
        {
            TenantId = tenantId,
            TicketNumber = await _ticketNumberGenerator.NextAsync(tenant.TicketPrefix, cancellationToken),
            OrganizationId = request.OrganizationId,
            OrganizationDepartmentId = request.OrganizationDepartmentId,
            DepartmentName = department?.Name ?? string.Empty,
            OrganizationContactId = request.OrganizationContactId,
            ContactFirstName = contact?.FirstName ?? string.Empty,
            ContactLastName = contact?.LastName ?? string.Empty,
            RawSenderEmail = contact?.Email ?? string.Empty,
            MailboxId = null,
            Subject = request.Subject.Trim(),
            Status = request.Status,
            Escalated = request.Escalated,
            Priority = priority,
            CreatorId = teamMember.Id,
            CreatorName = teamMember.FirstName + " " + teamMember.LastName,
            Source = TicketSource.Manual,
            AssignedToTeamMemberId = request.AssignedToTeamMemberId ?? assignedToTeamMemberId,
            DueAt = dueAt
        };

        ticket.StatusHistory.Add(new TicketStatusHistory
        {
            TenantId = tenantId,
            TicketId = ticket.Id,
            FromStatus = TicketStatus.Unverified,
            ToStatus = TicketStatus.New,
            ChangedByTeamMemberId = _currentUserService.TeamMemberId,
            ChangedAt = now,
            Note = "RouteBoard Ticket Created manually"
        });

        ticket.Messages.Add(new TicketMessage
        {
            TenantId = tenantId,
            TicketId = ticket.Id,
            Direction = MessageDirection.Inbound,
            AuthorType = contact is not null ? MessageAuthorType.OrganizationContact : MessageAuthorType.TeamMember,
            AuthorTeamMemberId = contact is null ? _currentUserService.TeamMemberId : null,
            AuthorEmail = contact?.Email,
            AuthorName = contact?.FullName,
            Body = _htmlSanitizer.Sanitize(request.InitialMessageBody)
        });




        _context.Tickets.Add(ticket);

        // Only create a notification here when the ticket is unassigned.
        // Assigned ticket notifications will be handled by TicketAssignedEvent.
        if (!ticket.AssignedToTeamMemberId.HasValue)
        {
            _context.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                TeamMemberId = teamMember.Id,
                TicketId = ticket.Id,
                Title = "Ticket Created",
                Message = $"Ticket {ticket.TicketNumber} was created and is awaiting assignment.",
                IsRead = false,
                CreatedAt = now
            });
        }

        await _context.SaveChangesAsync(cancellationToken);

        await _publisher.Publish(
            new TicketCreatedEvent(tenantId, ticket.Id, ticket.TicketNumber, ticket.Subject, IsUnverified: false),
            cancellationToken);

        if (ticket.AssignedToTeamMemberId.HasValue)
        {
            await _publisher.Publish(
                new TicketAssignedEvent(
                    tenantId,
                    ticket.Id,
                    ticket.TicketNumber,
                    ticket.AssignedToTeamMemberId.Value),
                cancellationToken);
        }

        return ticket.Id;
    }
}
