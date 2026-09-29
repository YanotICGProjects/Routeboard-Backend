using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POSShopTicketing.Application.OrganizationContacts.Commands.UpdateOrganizationContact;
using POSShopTicketing.Application.OrganizationDepartments.Commands.UpdateOrganizationDepartment;
using POSShopTicketing.Application.OrganizationMembers.Commands.CreateOrganizationMember;
using POSShopTicketing.Application.OrganizationMembers.Commands.MergeOrganizationMember;
using POSShopTicketing.Application.OrganizationMembers.Queries.GetOrganizationMembers;
using POSShopTicketing.Application.Organizations.Commands.CreateOrganization;
using POSShopTicketing.Application.Organizations.Commands.ReactivateOrganization;
using POSShopTicketing.Application.Organizations.Commands.ReactivateOrganizationContact;
using POSShopTicketing.Application.Organizations.Commands.ReactivateOrganizationDepartment;
using POSShopTicketing.Application.Organizations.Commands.SuspendOrganization;
using POSShopTicketing.Application.Organizations.Commands.SuspendOrganizationContact;
using POSShopTicketing.Application.Organizations.Commands.SuspendOrganizationDepartment;
using POSShopTicketing.Application.Organizations.Commands.UpdateOrganization;
using POSShopTicketing.Application.Organizations.Commands.UpdateOrganizationContact;
using POSShopTicketing.Application.Organizations.Commands.UpdateOrganizationDepartment;
using POSShopTicketing.Application.Organizations.Queries.GetOrganizationById;
using POSShopTicketing.Application.Organizations.Queries.GetOrganizations;
using POSShopTicketing.Application.OrganizationTeams.Commands.CreateOrganizationTeam;
using POSShopTicketing.Application.OrganizationTeams.Queries.GetOrganizationTeams;
using POSShopTicketing.Shared.Wrappers;

namespace POSShopTicketing.Api.Controllers;

/// <summary>Client Organizations, and nested under them, Organization
/// Teams and Organization Members. "Admin: Manage ... orgs, org teams" -
/// writes are Owner/Admin only; reads are open to any tenant role.</summary>
public class OrganizationsController : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<OrganizationDto>>> GetOrganizations(
        [FromQuery] string? searchTerm, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        var result = await Mediator.Send(new GetOrganizationsQuery { SearchTerm = searchTerm, PageNumber = pageNumber, PageSize = pageSize });
        return Ok(PaginatedResponse<OrganizationDto>.Create(result.Items, result.PageNumber, pageSize, result.TotalCount));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<OrganizationDto>>> GetOrganization(Guid id)
    {
        var result = await Mediator.Send(new GetOrganizationByIdQuery(id));
        return Ok(ApiResponse<OrganizationDto>.Success(result));
    }

    [HttpGet("contacts")]
    public async Task<ActionResult<PaginatedResponse<OrganizationContactDto>>> GetOrganizationContacts(
    [FromQuery] string? searchTerm,
    [FromQuery] int pageNumber = 1,
    [FromQuery] int pageSize = 20)
    {
        pageSize = Math.Clamp(pageSize, 1, 20);

        var result = await Mediator.Send(new GetOrganizationContactsQuery
        {
            SearchTerm = searchTerm,
            PageNumber = pageNumber,
            PageSize = pageSize
        });

        return Ok(
            PaginatedResponse<OrganizationContactDto>.Create(
                result.Items,
                result.PageNumber,
                pageSize,
                result.TotalCount));
    }


    [Authorize(Roles = "Owner,Admin")]
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateOrganizationDto>>> CreateOrganization(CreateOrganizationCommand command)
    {
        var organization = await Mediator.Send(command);
        var response = ApiResponse<CreateOrganizationDto>.Success(organization, "Organization created.");
        return CreatedAtAction(nameof(GetOrganization),new { id = organization.Id },response);
    }

    [Authorize(Roles = "Owner,Admin")]
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateOrganization(
     Guid id,
     [FromBody] UpdateOrganizationRequest request,
     CancellationToken cancellationToken)
    {
        var command = new UpdateOrganizationCommand
        {
            Id = id,
            Name = request.Name,
            Domain = request.Domain,
            Status = request.Status
        };

        var result = await Mediator.Send(command, cancellationToken);

        return Ok(result);
    }
    // ---- Organization Teams -------------------------------------------

    [HttpGet("{id:guid}/departments")]
    public async Task<ActionResult<PaginatedResponse<OrganizationDepartmentDto>>> GetDepartments(
    Guid id,
    [FromQuery] int pageNumber = 1,
    [FromQuery] int pageSize = 20)
    {
        var result = await Mediator.Send(
            new GetOrganizationDepartmentsQuery(
                id,
                pageNumber,
                pageSize));

        return Ok(result);
    }

    [Authorize(Roles = "Owner,Admin")]
    [HttpPost("{id:guid}/departments")]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateDepartment(Guid id, [FromBody] CreateDepartmentRequest request)
    {
        var teamId = await Mediator.Send(new CreateOrganizationDepartmentCommand { OrganizationId = id, Name = request.Name });
        return Ok(ApiResponse<Guid>.Success(teamId, "Department created."));
    }

    // ---- Organization Members ------------------------------------------

    [HttpGet("{id:guid}/contacts")]
    public async Task<ActionResult<PaginatedResponse<OrganizationContactDto>>> GetContacts(
        Guid id, [FromQuery] string? searchTerm, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        var result = await Mediator.Send(new GetOrganizationContactsQuery
        {
            OrganizationId = id, SearchTerm = searchTerm, PageNumber = pageNumber, PageSize = pageSize
        });
        return Ok(PaginatedResponse<OrganizationContactDto>.Create(result.Items, result.PageNumber, pageSize, result.TotalCount));
    }

    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPost("{id:guid}/contacts")]
    public async Task<ActionResult<ApiResponse<Guid>>> CreateContact(Guid id, [FromBody] CreateContactRequest request)
    {
        var contactId = await Mediator.Send(new CreateOrganizationContactCommand
        {
            OrganizationId = id,
            OrganizationDepartmentId = request.OrganizationDepartmentId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            Phone = request.Phone
        });
        return Ok(ApiResponse<Guid>.Success(contactId, "Contact created."));
    }

    /// <summary>Dedupe an alternate/typo'd address for someone already known.</summary>
    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPost("{id:guid}/contacts/{contactId:guid}/merge")]
    public async Task<ActionResult<ApiResponse<object>>> MergeContact(Guid id, Guid contactId, [FromBody] Guid mergeIntoContactId)
    {
        await Mediator.Send(new MergeOrganizationContactCommand(id, contactId, mergeIntoContactId));
        return Ok(ApiResponse<object>.Success(new { }, "Contacts merged."));
    }

    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPatch("{id:guid}/suspend")]
    public async Task<ActionResult<ApiResponse<object>>> SuspendOrganization(Guid id)
    {
        await Mediator.Send(new SuspendOrganizationCommand(id));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Organization suspended."));
    }


    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPatch("{id:guid}/reactivate")]
    public async Task<ActionResult<ApiResponse<object>>> ReactivateOrganization(Guid id)
    {
        await Mediator.Send(new ReactivateOrganizationCommand(id));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Organization reactivated."));
    }

    [HttpPut("{organizationId:guid}/departments/{departmentId:guid}")]
    public async Task<IActionResult> UpdateDepartment(
        Guid organizationId,
        Guid departmentId,
        [FromBody] UpdateOrganizationDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateOrganizationDepartmentCommand
        {
            OrganizationId = organizationId,
            DepartmentId = departmentId,
            Name = request.Name
        };

        var result = await Mediator.Send(command, cancellationToken);

        return Ok(result);
    }


    [HttpPatch("{departmentId:guid}/suspend-department")]
    public async Task<ActionResult<ApiResponse<object>>> SuspendDepartment(Guid departmentId)
    {
        await Mediator.Send(new SuspendOrganizationDepartmentCommand(departmentId));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Department suspended."));
    }

    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPatch("{departmentId:guid}/reactivate-department")]
    public async Task<ActionResult<ApiResponse<object>>> ReactivateOrganizationDepartment(Guid departmentId)
    {
        await Mediator.Send(new ReactivateOrganizationDepartmentCommand(departmentId));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Organization Department reactivated."));
    }

    [HttpPut("{organizationId}/contacts/{contactId:guid}")]
    public async Task<IActionResult> UpdateContact(Guid organizationId,Guid contactId,
        [FromBody] UpdateOrganizationContactRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateOrganizationContactCommand
        {
            OrganizationId = organizationId,
            ContactOrganizationId = contactId,
            FirstName = request.FirstName,
            LastName = request.LastName,
            Email = request.Email,
            JobTitle = request.JobTitle,
            Phone = request.Phone
        };

        var result = await Mediator.Send(command, cancellationToken);

        return Ok(result);
    }

    [HttpPatch("{contactId:guid}/suspend-contact")]
    public async Task<ActionResult<ApiResponse<object>>> SuspendContact(Guid contactId)
    {
        await Mediator.Send(new SuspendOrganizationContactComand(contactId));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Contact suspended."));
    }

    [Authorize(Roles = "Owner,Admin,Manager")]
    [HttpPatch("{contactId:guid}/reactivate-contact")]
    public async Task<ActionResult<ApiResponse<object>>> ReactivateOrganizationContact(Guid contactId)
    {
        await Mediator.Send(new ReactivateOrganizationContactCommand(contactId));

        return Ok(ApiResponse<object>.Success(
            new { },
            "Organization Contact reactivated."));
    }


}

public record CreateDepartmentRequest(string Name);
public record CreateContactRequest(Guid? OrganizationDepartmentId, string FirstName, string LastName, string Email, string? Phone);
