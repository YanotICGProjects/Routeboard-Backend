using Microsoft.Extensions.Logging;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;

namespace POSShopTicketing.Infrastructure.Services;

/// <summary>
/// Default IEmailSender: logs the outbound message instead of actually
/// sending it. Active whenever "Smtp:Host" isn't configured (see
/// DependencyInjection.AddInfrastructure) - the safe zero-config
/// default. Configure Smtp:* to switch to SmtpEmailSender instead, which
/// works with any SMTP-capable provider.
/// </summary>
public class EmailSender : IEmailSender
{
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(ILogger<EmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAutoReplyAsync(
    string toEmail,
    string? toName,
    string subject,
    string bodyHtml,
    string replyTo,
    CancellationToken cancellationToken)
    {
        return SendAsync(
            toEmail,
            toName,
            subject,
            bodyHtml,
            cancellationToken);
    }

    public Task SendReplyAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "OUTBOUND EMAIL to {ToName} <{ToEmail}> | Subject: {Subject} | " +
            "Reply-To: {FromAlias} | In-Reply-To: {InReplyTo} | Message-Id: {MessageId}\n{Body}",
            message.ToName, message.ToEmail, message.Subject, message.FromAlias,
            message.InReplyToMessageId ?? "(none - new thread)", message.GeneratedMessageId, message.BodyHtml);

        return Task.CompletedTask;
    }

    public Task SendAsync(string toEmail, string? toName, string subject, string bodyHtml, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "OUTBOUND EMAIL to {ToName} <{ToEmail}> | Subject: {Subject}\n{Body}",
            toName, toEmail, subject, bodyHtml);

        return Task.CompletedTask;
    }
}
