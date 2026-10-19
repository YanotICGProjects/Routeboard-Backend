using FluentValidation;
using Microsoft.EntityFrameworkCore;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Tickets.Commands.CreateTicket;

public class CreateTicketCommandValidator : AbstractValidator<CreateTicketCommand>
{
    private readonly IApplicationDbContext _context;

    public CreateTicketCommandValidator(IApplicationDbContext context)
    {
        _context = context;

        RuleFor(v => v.Subject)
            .NotEmpty()
            .MaximumLength(250);

        RuleFor(v => v.InitialMessageBody)
            .NotEmpty()
            .MaximumLength(8000);
        RuleFor(x => x.OrganizationContactId)
    .NotNull()
    .When(x => x.isSendToCustomer)
    .WithMessage("An organization contact must be selected when sending an email to a customer.");

        RuleFor(x => x)
            .MustAsync(HaveContactWithEmail)
            .When(x => x.isSendToCustomer)
            .WithMessage("The selected contact does not have a valid email address.");

        RuleFor(v => v.Priority!.Value)
            .IsInEnum()
            .When(v => v.Priority.HasValue);

        RuleFor(v => v.AssignedToTeamMemberId)
            .MustAsync(BeAnActiveTeamMember)
            .When(v => v.AssignedToTeamMemberId.HasValue)
            .WithMessage("Assigned team member is disabled or has not accepted their invitation.");

        RuleFor(v => v.OrganizationId)
            .MustAsync(BeAnActiveOrganization)
            .When(v => v.OrganizationId.HasValue)
            .WithMessage("Organization is inactive or suspended.");

        RuleFor(v => v.OrganizationContactId)
            .MustAsync(BeAnActiveContact)
            .When(v => v.OrganizationContactId.HasValue)
            .WithMessage("Organization contact is inactive or suspended.");

        RuleFor(v => v.OrganizationDepartmentId)
            .MustAsync(BeAnActiveDepartment)
            .When(v => v.OrganizationDepartmentId.HasValue)
            .WithMessage("Organization department is suspended.");
    }

    private async Task<bool> HaveContactWithEmail(
    CreateTicketCommand command,
    CancellationToken cancellationToken)
    {
        if (!command.OrganizationContactId.HasValue)
            return false;

        return await _context.OrganizationContacts
            .AnyAsync(
                x => x.Id == command.OrganizationContactId.Value
                     && x.Status == OrganizationContactStatus.Active
                     && !string.IsNullOrWhiteSpace(x.Email),
                cancellationToken);
    }


    private async Task<bool> BeAnActiveTeamMember(
        Guid? teamMemberId,
        CancellationToken cancellationToken)
    {
        if (!teamMemberId.HasValue)
            return true;

        return await _context.TeamMembers
            .AnyAsync(
                x => x.Id == teamMemberId.Value
                     && x.Status == TeamMemberStatus.Active,
                cancellationToken);
    }

    private async Task<bool> BeAnActiveOrganization(
        Guid? organizationId,
        CancellationToken cancellationToken)
    {
        if (!organizationId.HasValue)
            return true;

        return await _context.Organizations
            .AnyAsync(
                x => x.Id == organizationId.Value
                     && x.Status == OrganizationStatus.Active,
                cancellationToken);
    }

    private async Task<bool> BeAnActiveContact(
        Guid? contactId,
        CancellationToken cancellationToken)
    {
        if (!contactId.HasValue)
            return true;

        return await _context.OrganizationContacts
            .AnyAsync(
                x => x.Id == contactId.Value
                     && x.Status == OrganizationContactStatus.Active,
                cancellationToken);
    }

    private async Task<bool> BeAnActiveDepartment(
        Guid? departmentId,
        CancellationToken cancellationToken)
    {
        if (!departmentId.HasValue)
            return true;

        return await _context.OrganizationDepartments
            .AnyAsync(
                x => x.Id == departmentId.Value
                     && x.Status != OrganizationDepartmentStatus.Suspended,
                cancellationToken);
    }
}