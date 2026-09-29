using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;

namespace POSShopTicketing.Infrastructure.Services;

/// <summary>
/// Generic SMTP IEmailSender via MailKit (the maintained replacement for
/// the old, no-longer-recommended System.Net.Mail.SmtpClient) - works
/// with any SMTP-capable provider. Active whenever "Smtp:Host" is
/// configured (see DependencyInjection.AddInfrastructure); each send
/// opens a short-lived connection rather than pooling one, which is
/// simple and correct at this scale (ticket replies + audit emails, not
/// bulk marketing volume) - swap for a pooled/queued client first if
/// outbound volume ever becomes the bottleneck.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpSettings> settings, ILogger<SmtpEmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task SendReplyAsync(OutboundEmailMessage message, CancellationToken cancellationToken)
    {
        var mime = BuildMessage(message.ToEmail, message.ToName, message.Subject, message.BodyHtml);

        // The per-ticket alias is where the customer's reply lands next
        // time - see WebhooksController/IngestInboundEmailCommand.
        mime.ReplyTo.Add(MailboxAddress.Parse(message.FromAlias));

        if (!string.IsNullOrWhiteSpace(message.InReplyToMessageId))
        {
            mime.InReplyTo = message.InReplyToMessageId;
            mime.References.Add(message.InReplyToMessageId);
        }

        mime.MessageId = message.GeneratedMessageId.Trim('<', '>');

        return SendAsync(mime, cancellationToken);
    }

    public Task SendAsync(string toEmail, string? toName, string subject, string bodyHtml, CancellationToken cancellationToken) =>
        SendAsync(BuildMessage(toEmail, toName, subject, bodyHtml), cancellationToken);

    private MimeMessage BuildMessage(string toEmail, string? toName, string subject, string bodyHtml)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
        mime.To.Add(new MailboxAddress(toName ?? toEmail, toEmail));
        mime.Subject = subject;
        mime.Body = new BodyBuilder { HtmlBody = bodyHtml }.ToMessageBody();
        return mime;
    }

    private async Task SendAsync(MimeMessage mime, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new SmtpClient();

            await client.ConnectAsync(
                _settings.Host, _settings.Port,
                _settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(_settings.Username))
            {
                await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Outbound email failing should never take down the request
            // that triggered it (a ticket reply, a login audit email) -
            // log it and move on rather than throwing.
            _logger.LogError(ex, "Failed to send email to {Recipients} via SMTP {Host}:{Port}",
                string.Join(", ", mime.To), _settings.Host, _settings.Port);
        }
    }
}
