using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Application.Tickets.Common;
using POSShopTicketing.Infrastructure.Inbound;
using POSShopTicketing.Infrastructure.Services;

namespace POSShopTicketing.Infrastructure.Email;

// =====================================================================
//  NEW FILE: Infrastructure/Email/EmailRouting.cs
//
//  THE IDEA
//  --------
//  Your app sends two kinds of outgoing email through IEmailSender:
//
//    "Transactional"  = SendAsync(...)       invites, OTP codes, audit/login notices
//    "Ticket reply"   = SendReplyAsync(...)  an agent's reply on a ticket thread
//
//  Which provider carries each kind is decided by two settings - nothing to do
//  with the environment, so production behaves exactly like your laptop unless
//  you change the setting:
//
//    "Email": {
//      "TransactionalProvider": "Smtp",     // Smtp | Resend | Log   (default Smtp = Gmail)
//      "TicketReplyProvider":   "Smtp"      // Smtp | Resend | Log   (default Smtp = Gmail)
//    }
//
//  Out of the box EVERYTHING goes through your Gmail SMTP exactly as today.
//  Resend is then used only for what it is needed for: RECEIVING customer emails
//  and turning them into tickets (the inbound webhook + API calls, registered below).
//  If you ever want ticket replies to go out through Resend, change
//  TicketReplyProvider to "Resend" - no code change.
// =====================================================================

public enum EmailProviderKind { Log, Smtp, Resend }

/// <summary>Which provider was chosen for each kind of email. Printed at start-up and shown by the check endpoint.</summary>
public record EmailProviderInfo(EmailProviderKind Transactional, EmailProviderKind TicketReply);

public static class EmailServiceCollectionExtensions
{
    /// <summary>Outbound email: the three senders plus the router that picks between them.</summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpSettings>(configuration.GetSection(SmtpSettings.SectionName));
        services.Configure<ResendSenderOptions>(configuration.GetSection("Resend"));

        // The three concrete senders. Registering them does NOT make the app use them;
        // only the router below decides that.
        services.AddScoped<SmtpEmailSender>();      // your existing Gmail/any-SMTP sender
        services.AddScoped<EmailSender>();          // your existing log-only sender
        services.AddScoped<ResendEmailSender>();    // Resend's HTTP API

        // Direct-to-Resend access, used only by the Development test endpoint (POST /api/email/send).
        services.AddScoped<IOutboundEmailSender>(sp => sp.GetRequiredService<ResendEmailSender>());

        // Work out the routing ONCE, at start-up, so a typo or missing key fails immediately.
        var info = new EmailProviderInfo(
            Transactional: Resolve(configuration, "Email:TransactionalProvider"),
            TicketReply: Resolve(configuration, "Email:TicketReplyProvider"));
        services.AddSingleton(info);

        // The ONE registration for IEmailSender in the whole app. (If another line anywhere
        // registers IEmailSender, the LAST registration wins - so there must be only this one.)
        services.AddScoped<IEmailSender, RoutingEmailSender>();

        return services;
    }

    /// <summary>
    /// Inbound email through Resend: the settings, the API client that fetches received
    /// emails/attachments, and the adapter the webhook controller uses.
    /// </summary>
    public static IServiceCollection AddResendInbound(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ResendOptions>(configuration.GetSection("Resend"));

        // Shared secret used by the SendGrid/Postmark webhook endpoints ("InboundEmail:WebhookSecret").
        // Without this binding every webhook call is rejected with 401.
        services.Configure<InboundEmailOptions>(configuration.GetSection("InboundEmail"));

        // A named HttpClient that already carries the Resend API key. Used both to fetch received emails
        // and (for the optional Resend sender) to send. Always registered so the API key is read in one place.
        services.AddHttpClient(ResendReceivingClient.ApiClientName, client =>
        {
            client.BaseAddress = new Uri(configuration["Resend:BaseUrl"] ?? "https://api.resend.com/");
            client.Timeout = TimeSpan.FromSeconds(30);

            var apiKey = configuration["Resend:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
        });
        services.AddHttpClient();   // plain client with NO api key: used to download attachment files

        services.AddScoped<ResendReceivingClient>();
        services.AddScoped<ResendInboundAdapter>();

        return services;
    }

    /// <summary>Reads one routing setting and turns it into a safe, validated provider.</summary>
    private static EmailProviderKind Resolve(IConfiguration configuration, string key)
    {
        var raw = configuration[key];

        // No setting = keep today's behaviour: SMTP (Gmail).
        var kind = EmailProviderKind.Smtp;
        if (!string.IsNullOrWhiteSpace(raw) && !Enum.TryParse(raw, ignoreCase: true, out kind))
        {
            throw new InvalidOperationException($"{key} must be Smtp, Resend or Log (got \"{raw}\").");
        }

        // "Smtp" but nothing configured: log instead of crashing (the same zero-config fallback you had before).
        if (kind == EmailProviderKind.Smtp && string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
        {
            return EmailProviderKind.Log;
        }

        // "Resend" but no key: refuse to start rather than silently losing emails.
        if (kind == EmailProviderKind.Resend && string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"]))
        {
            throw new InvalidOperationException($"{key} is Resend but Resend:ApiKey is not set.");
        }

        return kind;
    }
}

/// <summary>
/// The only IEmailSender the rest of the app ever sees. It owns no sending logic: for each call it
/// looks up which provider that kind of email is routed to and hands the message over.
/// </summary>
public class RoutingEmailSender : IEmailSender
{
    private readonly SmtpEmailSender _smtp;
    private readonly ResendEmailSender _resend;
    private readonly EmailSender _log;
    private readonly EmailProviderInfo _routing;

    public RoutingEmailSender(
        SmtpEmailSender smtp, ResendEmailSender resend, EmailSender log, EmailProviderInfo routing)
    {
        _smtp = smtp;
        _resend = resend;
        _log = log;
        _routing = routing;
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

    /// <summary>A reply on a ticket thread - goes to whichever provider TicketReplyProvider names.</summary>
    public Task SendReplyAsync(OutboundEmailMessage message, CancellationToken cancellationToken) =>
        Pick(_routing.TicketReply).SendReplyAsync(message, cancellationToken);

    /// <summary>Invites, OTPs, audit notices - goes to whichever provider TransactionalProvider names.</summary>
    public Task SendAsync(string toEmail, string? toName, string subject, string bodyHtml, CancellationToken cancellationToken) =>
        Pick(_routing.Transactional).SendAsync(toEmail, toName, subject, bodyHtml, cancellationToken);

    private IEmailSender Pick(EmailProviderKind kind) => kind switch
    {
        EmailProviderKind.Resend => _resend,
        EmailProviderKind.Smtp => _smtp,
        _ => _log
    };
}