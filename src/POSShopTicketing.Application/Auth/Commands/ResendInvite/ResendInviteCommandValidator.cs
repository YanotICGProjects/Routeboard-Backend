using FluentValidation;

namespace POSShopTicketing.Application.Auth.Commands.ResendInvite;

public sealed class ResendInviteCommandValidator : AbstractValidator<ResendInviteCommand>
{
    public ResendInviteCommandValidator()
    {
        RuleFor(x => x.TeamMemberId)
            .NotEmpty();
    }
}