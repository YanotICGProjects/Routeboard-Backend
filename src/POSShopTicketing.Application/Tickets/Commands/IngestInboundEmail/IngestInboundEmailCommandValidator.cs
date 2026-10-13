using FluentValidation;

namespace POSShopTicketing.Application.Tickets.Commands.IngestInboundEmail;

public class IngestInboundEmailCommandValidator : AbstractValidator<IngestInboundEmailCommand>
{
    public IngestInboundEmailCommandValidator()
    {
        //RuleFor(v => v.MailboxId).NotEmpty();
        //RuleFor(v => v.WebhookSecret).NotEmpty();
        RuleFor(v => v.SenderEmail).NotEmpty().EmailAddress();
        RuleFor(v => v.Subject).MaximumLength(250);
        RuleFor(v => v.BodyHtml).NotEmpty();
        RuleFor(v => v.MessageId).NotEmpty().MaximumLength(500);
    }
}
