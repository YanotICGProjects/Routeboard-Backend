using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Tickets.Commands.AddTicketMessage;
using POSShopTicketing.Application.Tickets.Commands.ChangeTicketAssignee;
using POSShopTicketing.Application.Tickets.Commands.ChangeTicketEscalation;
using POSShopTicketing.Application.Tickets.Commands.ChangeTicketPriority;
using POSShopTicketing.Application.Tickets.Commands.ChangeTicketStatus;
using POSShopTicketing.Application.Tickets.Commands.ChangeTicketTransportation;
using POSShopTicketing.Application.Tickets.Commands.CreateTicket;
using POSShopTicketing.Application.Tickets.Commands.Triage.AnonymizeTriageTicket;
using POSShopTicketing.Application.Tickets.Commands.Triage.LinkTriageTicket;
using POSShopTicketing.Application.Tickets.Commands.Triage.RegisterTriageTicket;
using POSShopTicketing.Application.Tickets.Commands.Triage.RejectTriageTicket;
using POSShopTicketing.Application.Tickets.Queries.GetSlaBreachedTickets;
using POSShopTicketing.Application.Tickets.Queries.GetTicketById;
using POSShopTicketing.Application.Tickets.Queries.GetTicketConversation;
using POSShopTicketing.Application.Tickets.Queries.GetTicketMessages;
using POSShopTicketing.Application.Tickets.Queries.GetTickets;
using POSShopTicketing.Application.Tickets.Queries.GetTicketStatusHistory;
using POSShopTicketing.Application.Tickets.Queries.GetTicketThread;
using POSShopTicketing.Application.Tickets.Queries.GetTriageQueue;
using POSShopTicketing.Domain.Enums;
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers;

/// <summary>
/// The work item, end to end: the working queue, manual creation, the
/// message thread, status/priority/assignee/escalation changes, and -
/// nested under /triage - the four Unverified-sender decisions
/// (Register/Link/Anonymize/Reject), Manager+ only.
/// </summary>
public class TicketsController : ApiControllerBase
{
    private readonly ICurrentUserService _currentUserService;

    public TicketsController(ICurrentUserService currentUserService)
    {
        _currentUserService = currentUserService;
    }

    /// <summary>GET /tickets?status=&priority=&assignee=&org= - the
    /// normal working queue. An Agent can only ever see their own
    /// assigned tickets here ("notably cannot: see tickets outside their
    /// team") - any assignedToTeamMemberId they pass is overridden.</summary>
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<TicketDto>>> GetTickets(
        [FromQuery] string? searchTerm,
        [FromQuery] TicketStatus? status,
        [FromQuery] TicketPriority? priority,
        [FromQuery] Guid? organizationId,
        [FromQuery] Guid? assignedToTeamMemberId,
        [FromQuery] bool? escalated,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20)
    {
        //if (_currentUserService.Role == TeamMemberRole.Agent)
        //{
        //    assignedToTeamMemberId = _currentUserService.TeamMemberId;
        //}

        var result = await Mediator.Send(new GetTicketsQuery
        {
            SearchTerm = searchTerm,
            Status = status,
            Priority = priority,
            OrganizationId = organizationId,
            AssignedToTeamMemberId = assignedToTeamMemberId,
            Escalated = escalated,
            PageNumber = pageNumber,
            PageSize = pageSize
        });

        return Ok(PaginatedResponse<TicketDto>.Create(result.Items, result.PageNumber, pageSize, result.TotalCount));
    }

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpGet("sla-breaches")]
    public async Task<ActionResult<ApiResponse<List<TicketDto>>>> GetSlaBreaches()
    {
        var result = await Mediator.Send(new GetSlaBreachedTicketsQuery());
        return Ok(ApiResponse<List<TicketDto>>.Success(result));
    }

    /// <summary>Manager/Admin/Owner-visible triage queue for Unverified tickets.</summary>
    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpGet("triage")]
    public async Task<ActionResult<PaginatedResponse<TicketDto>>> GetTriageQueue(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        var result = await Mediator.Send(new GetTriageQueueQuery { PageNumber = pageNumber, PageSize = pageSize });
        return Ok(PaginatedResponse<TicketDto>.Create(result.Items, result.PageNumber, pageSize, result.TotalCount));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<TicketDto>>> GetTicket(Guid id)
    {
        var result = await Mediator.Send(new GetTicketByIdQuery(id));
        return Ok(ApiResponse<TicketDto>.Success(result));
    }

    /// <summary>Manual creation - usable end to end before email is wired up.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateTicket(CreateTicketCommand command)
    {
        var id = await Mediator.Send(command);
        var response = ApiResponse<Guid>.Success(id, "Ticket created.");
        return CreatedAtAction(nameof(GetTicket), new { id }, response);
    }



    //[HttpGet("{ticketNumber}/conversation")]
    //public async Task<IActionResult> GetConversation(
    //string ticketNumber,
    //CancellationToken ct)
    //{
    //    var result = await Mediator.Send(
    //        new GetTicketConversationQuery(ticketNumber),
    //        ct);

    //    return Ok(result);
    //}

    [HttpGet("{ticketNumber}/thread")]
    public async Task<IActionResult> GetThread(
    string ticketNumber,
    CancellationToken cancellationToken)
    {
        var result = await Mediator.Send(
            new GetTicketThreadQuery(ticketNumber),
            cancellationToken);

        return Ok(result);
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeStatus(Guid id, [FromBody] ChangeStatusRequest request)
    {
        await Mediator.Send(new ChangeTicketStatusCommand(id, request.Status, request.Note));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket status updated."));
    }

    [HttpPatch("{id:guid}/priority")]
    public async Task<ActionResult<ApiResponse<object>>> ChangePriority(Guid id, [FromBody] TicketPriority priority)
    {
        await Mediator.Send(new ChangeTicketPriorityCommand(id, priority));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket priority updated."));
    }

    /// <summary>Manager can reassign any ticket on their team; an Agent cannot.</summary>
    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPatch("{id:guid}/assignee")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeAssignee(Guid id, [FromBody] Guid? assignedToTeamMemberId)
    {
        await Mediator.Send(new ChangeTicketAssigneeCommand(id, assignedToTeamMemberId));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket assignee updated."));
    }

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPatch("{id:guid}/escalate")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeEscalation(Guid id, [FromBody] bool escalated)
    {
        await Mediator.Send(new ChangeTicketEscalationCommand(id, escalated));
        return Ok(ApiResponse<object>.Success(new { }, escalated ? "Ticket escalated." : "Ticket un-escalated."));
    }

    [HttpGet("{id:guid}/status-history")]
    public async Task<ActionResult<ApiResponse<List<TicketStatusHistoryDto>>>> GetStatusHistory(Guid id)
    {
        var result = await Mediator.Send(new GetTicketStatusHistoryQuery(id));
        return Ok(ApiResponse<List<TicketStatusHistoryDto>>.Success(result));
    }

    [HttpGet("{id:guid}/messages")]
    public async Task<ActionResult<ApiResponse<List<TicketMessageDto>>>> GetMessages(
        Guid id, [FromQuery] bool includeInternalNotes = true)
    {
        var result = await Mediator.Send(new GetTicketMessagesQuery(id, includeInternalNotes));
        return Ok(ApiResponse<List<TicketMessageDto>>.Success(result));
    }

    /// <summary>Reply or internal note.</summary>
    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<ApiResponse<Guid>>> AddMessage(Guid id, [FromBody] AddMessageRequest request)
    {
        var messageId = await Mediator.Send(new AddTicketMessageCommand
        {
            TicketId = id,
            Direction = request.Direction,
            Body = request.Body
        });

        return Ok(ApiResponse<Guid>.Success(messageId, "Message added."));
    }

    // ---- Triage: Unverified queue, Manager+ only -----------------------

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPost("{id:guid}/triage/register")]
    public async Task<ActionResult<ApiResponse<object>>> TriageRegister(Guid id, [FromBody] TriageRegisterRequest request)
    {
        await Mediator.Send(new RegisterTriageTicketCommand
        {
            TicketId = id,
            OrganizationId = request.OrganizationId,
            NewOrganizationName = request.NewOrganizationName,
            OrganizationTeamId = request.OrganizationTeamId,
            NewOrganizationTeamName = request.NewOrganizationTeamName,
            MemberFullName = request.MemberFullName
        });
        return Ok(ApiResponse<object>.Success(new { }, "Sender registered and ticket linked."));
    }

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPost("{id:guid}/triage/link")]
    public async Task<ActionResult<ApiResponse<object>>> TriageLink(Guid id, [FromBody] Guid organizationMemberId)
    {
        await Mediator.Send(new LinkTriageTicketCommand(id, organizationMemberId));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket linked to existing member."));
    }

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPost("{id:guid}/triage/anonymize")]
    public async Task<ActionResult<ApiResponse<object>>> TriageAnonymize(Guid id, [FromBody] Guid assignedToTeamMemberId)
    {
        await Mediator.Send(new AnonymizeTriageTicketCommand(id, assignedToTeamMemberId));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket assigned as an anonymous one-off."));
    }

    [Authorize(Roles = "Manager,Admin,Owner")]
    [HttpPost("{id:guid}/triage/reject")]
    public async Task<ActionResult<ApiResponse<object>>> TriageReject(Guid id, [FromBody] string? reason)
    {
        await Mediator.Send(new RejectTriageTicketCommand(id, reason));
        return Ok(ApiResponse<object>.Success(new { }, "Ticket rejected."));
    }

    [HttpPatch("{id:guid}/transportation")]
    public async Task<ActionResult<ApiResponse<object>>> ChangeTransportation(
    Guid id,
    [FromBody] ChangeTransportationRequest request)
    {
        await Mediator.Send(
            new ChangeTicketTransportationCommand(
                id,
                request.TransportationRoute,
                request.TransportationAmount));

        return Ok(
            ApiResponse<object>.Success(
                new { },
                "Transportation details updated."));
    }
}

public record ChangeStatusRequest(TicketStatus Status, string? Note = null);
public record AddMessageRequest(MessageDirection Direction, string Body);
public record TriageRegisterRequest(
    Guid? OrganizationId, string? NewOrganizationName,
    Guid? OrganizationTeamId, string? NewOrganizationTeamName,
    string MemberFullName);
public record ChangeTransportationRequest(
    string TransportationRoute,
    string TransportationAmount);
