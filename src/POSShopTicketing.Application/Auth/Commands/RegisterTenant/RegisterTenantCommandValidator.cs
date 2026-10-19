using FluentValidation;
using POSShopTicketing.Domain.Enums;
using System.Net.Mail;

namespace POSShopTicketing.Application.Auth.Commands.RegisterTenant;

public class RegisterTenantCommandValidator : AbstractValidator<RegisterTenantCommand>
{
    public RegisterTenantCommandValidator()
    {
        RuleFor(v => v.TenantName)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(v => v.OwnerEmail)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(200)
            .Must(BeValidEmail)
            .WithMessage("Please provide a valid email address.");

        RuleFor(v => v.OwnerPassword)
            .NotEmpty()
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters.")
            .MaximumLength(100);

        RuleFor(v => v.WorkSpaceName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(v => v.OwnerFirstName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(v => v.OwnerLastName)
            .NotEmpty()
            .MaximumLength(100);

        RuleForEach(v => v.Invites)
            .ChildRules(invite =>
            {
                invite.When(x => !string.IsNullOrWhiteSpace(x.Email), () =>
                {
                    invite.RuleFor(x => x.Email)
                        .NotEmpty()
                        .EmailAddress()
                        .Must(BeValidEmail)
                        .WithMessage("Please provide a valid Invite email address.");

                    invite.RuleFor(x => x.Role)
                        .NotNull()
                        .WithMessage("Invite Role is required.").NotEqual(TeamMemberRole.PlatformSuperAdmin);
                });
            });

        RuleFor(v => v)
            .Must(HaveInviteEmailsDifferentFromOwner)
            .WithMessage("Invite email address cannot be the same as the owner email.");

        RuleFor(v => v)
            .Must(HaveUniqueInviteEmails)
            .WithMessage("Duplicate invite email addresses are not allowed.");
    }

    private static bool BeValidEmail(string email)
    {
        try
        {
            var address = new MailAddress(email);

            return address.Address.Equals(
                email,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HaveInviteEmailsDifferentFromOwner(RegisterTenantCommand command)
    {
        var ownerEmail = command.OwnerEmail.Trim().ToLowerInvariant();

        return command.Invites
            .Where(i => !string.IsNullOrWhiteSpace(i.Email))
            .All(i => i.Email!.Trim().ToLowerInvariant() != ownerEmail);
    }

    private static bool HaveUniqueInviteEmails(RegisterTenantCommand command)
    {
        var inviteEmails = command.Invites
            .Where(i => !string.IsNullOrWhiteSpace(i.Email))
            .Select(i => i.Email!.Trim().ToLowerInvariant())
            .ToList();

        return inviteEmails.Count == inviteEmails.Distinct().Count();
    }
}