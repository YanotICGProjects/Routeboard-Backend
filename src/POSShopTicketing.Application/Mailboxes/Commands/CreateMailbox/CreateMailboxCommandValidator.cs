using FluentValidation;

namespace POSShopTicketing.Application.Mailboxes.Commands.CreateMailbox;

public class CreateMailboxCommandValidator : AbstractValidator<CreateMailboxCommand>
{
    public CreateMailboxCommandValidator()
    {
        // null = zero-setup system address. If a value IS supplied it must be a real address.
        When(v => v.EmailAddress is not null, () =>
        {
            RuleFor(v => v.EmailAddress)
                .NotEmpty().WithMessage("Omit the email address to get the system address, or enter a valid one.")
                .MaximumLength(200)
                .EmailAddress();
        });

        RuleFor(v => v.Provider).IsInEnum();
    }
}
