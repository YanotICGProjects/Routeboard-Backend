using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Common.Interfaces;
using POSShopTicketing.Application.Common.Models;
using POSShopTicketing.Infrastructure.Inbound;

namespace POSShopTicketing.Infrastructure.Email;

// =====================================================================
//  REPLACES Infrastructure/Email/ResendEmailSender.cs (the whole file).
//
//  What changed from the first version:
//    1. It now implements BOTH methods of your real IEmailSender:
//         SendReplyAsync(OutboundEmailMessage, ...)  - ticket-thread replies
//         SendAsync(toEmail, toName?, subject, bodyHtml, ...) - everything else
//       (that missing method was your CS0535 error; toName is now nullable too).
//    2. It behaves like your SmtpEmailSender: if sending fails, the error is LOGGED
//       and the request that triggered it carries on. A failed audit email must not
//       break a login.
//    3. The only code that THROWS on failure is IOutboundEmailSender, which your
//       /api/email/send test endpoint uses, so a test shows the real error.
//
//  Whether this class or SmtpEmailSender is used for IEmailSender is decided in
//  EmailServiceCollectionExtensions.cs (file 11) - not here.
// =====================================================================

/// <summary>Bound from the "Resend" appsettings section (same section as ResendOptions).</summary>
public class ResendSenderOptions
{
    /// <summary>Default From. Must be on a domain verified for SENDING in Resend.</summary>
    public string FromAddress { get; set; } = "noreply@support.routeboard.online";
    public string FromName { get; set; } = "RouteBoard";

    /// <summary>Reply-To for platform emails (invites, audit notices) so replies reach your pos-shop inbox.</summary>
    public string? ReplyTo { get; set; }

    /// <summary>Optional sending-only key. Leave empty to reuse Resend:ApiKey.</summary>
    public string? SendApiKey { get; set; }

    public int MaxAttempts { get; set; } = 3;
}

/// <summary>A provider-neutral outgoing email, used by the test endpoint and internally.</summary>
public record OutboundEmail(
    string ToEmail,
    string? ToName,
    string Subject,
    string Html,
    string? Text = null,
    string? FromEmail = null,
    string? FromName = null,
    string? ReplyTo = null,
    string? MessageId = null,      // OUR Message-ID (brackets optional)
    string? InReplyTo = null,
    string? References = null);

public interface IOutboundEmailSender
{
    /// <summary>Sends and THROWS on failure. Returns Resend's email id.</summary>
    Task<string?> SendAsync(OutboundEmail email, CancellationToken ct);
}

public class EmailSendException : Exception
{
    public EmailSendException(string message) : base(message) { }
}

public class ResendEmailSender : IEmailSender, IOutboundEmailSender
{
    private static readonly Regex BreakTags =
        new(@"<\s*(br|/p|/div|/tr|/h[1-6]|/li)\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new("<[^>]+>", RegexOptions.Compiled);

    private readonly IHttpClientFactory _factory;
    private readonly ResendSenderOptions _options;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        IHttpClientFactory factory, IOptions<ResendSenderOptions> options, ILogger<ResendEmailSender> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    //  IEmailSender (what the rest of your app calls)
    // ------------------------------------------------------------------

    /// <summary>
    /// A reply on a ticket thread. Mirrors what SmtpEmailSender does:
    ///   - From     = the default sender (FromAddress / FromName)
    ///   - Reply-To = message.FromAlias (the per-ticket alias the customer's answer should go to)
    ///   - Message-ID  = message.GeneratedMessageId  (we choose it, so we can match replies later)
    ///   - In-Reply-To / References = the customer's previous message, so mail clients thread it
    /// </summary>
    public async Task SendReplyAsync(
     OutboundEmailMessage message,
     CancellationToken cancellationToken)
    {
        await SendCoreAsync(
            new OutboundEmail(
                ToEmail: message.ToEmail,
                ToName: message.ToName,

                FromEmail: message.FromEmail,
                FromName: message.FromName,

                Subject: message.Subject,
                Html: message.BodyHtml,

                ReplyTo: message.FromAlias,

                MessageId: message.GeneratedMessageId,

                InReplyTo: message.InReplyToMessageId,

                References: message.InReplyToMessageId),
            cancellationToken);
    }


    /// <summary>Any other transactional email (invites, audit notices, OTPs): no threading headers.</summary>
    public Task SendAsync(string toEmail, string? toName, string subject, string bodyHtml, CancellationToken cancellationToken) =>
        SendSafelyAsync(new OutboundEmail(toEmail, toName, subject, bodyHtml), cancellationToken);

    // ------------------------------------------------------------------
    //  IOutboundEmailSender (used by the /api/email/send test endpoint)
    // ------------------------------------------------------------------
    public Task<string?> SendAsync(OutboundEmail email, CancellationToken ct) => SendCoreAsync(email, ct);

    // ------------------------------------------------------------------
    //  Internals
    // ------------------------------------------------------------------

    /// <summary>Same contract as SmtpEmailSender: log the failure, never take down the caller.</summary>
    private async Task SendSafelyAsync(OutboundEmail email, CancellationToken ct)
    {
        try
        {
            await SendCoreAsync(email, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send email to {To} via Resend", email.ToEmail);
        }
    }

    /// <summary>Builds the Resend request, sends it, retries on 429/5xx, throws on final failure.</summary>
    private async Task<string?> SendCoreAsync(OutboundEmail email, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["from"] = FormatAddress(email.FromEmail ?? _options.FromAddress, email.FromName ?? _options.FromName),
            ["to"] = new[] { FormatAddress(email.ToEmail, email.ToName) },
            ["subject"] = OneLine(email.Subject),
            ["html"] = email.Html,
            // A plain-text twin of the HTML helps deliverability.
            ["text"] = string.IsNullOrWhiteSpace(email.Text) ? HtmlToText(email.Html) : email.Text!
        };

        var replyTo = email.ReplyTo ?? _options.ReplyTo;
        if (!string.IsNullOrWhiteSpace(replyTo)) payload["reply_to"] = replyTo!;

        // Threading headers (angle brackets are required by the email standard).
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(email.MessageId)) headers["Message-ID"] = Bracket(email.MessageId!);
        if (!string.IsNullOrWhiteSpace(email.InReplyTo)) headers["In-Reply-To"] = Bracket(email.InReplyTo!);
        if (!string.IsNullOrWhiteSpace(email.References)) headers["References"] = Bracket(email.References!);
        if (headers.Count > 0) payload["headers"] = headers;

        var json = JsonSerializer.Serialize(payload);

        // ONE idempotency key for every retry of this send: if a request times out but
        // actually succeeded, retrying can't deliver the email twice.
        // For ticket replies the Message-ID is unique per reply, so it is also safe across Hangfire retries.
        var idempotencyKey = string.IsNullOrWhiteSpace(email.MessageId)
            ? Guid.NewGuid().ToString()
            : email.MessageId!.Trim('<', '>');
        var http = _factory.CreateClient(ResendReceivingClient.ApiClientName);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", idempotencyKey);

            if (!string.IsNullOrWhiteSpace(_options.SendApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SendApiKey);
            }

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode) return TryReadId(body);

            // 429 (slow down) and 5xx (Resend having trouble) are worth another try.
            // Other 4xx (bad address, unverified domain, bad key) will fail every time.
            var retryable = response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500;
            if (retryable && attempt < _options.MaxAttempts)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning("Resend returned {Status}; retrying in {Delay}s (attempt {Attempt})",
                    (int)response.StatusCode, delay.TotalSeconds, attempt);
                await Task.Delay(delay, ct);
                continue;
            }

            _logger.LogError("Resend rejected an email to {To}: {Status} {Body}",
                email.ToEmail, (int)response.StatusCode, body);
            throw new EmailSendException($"Resend could not send the email ({(int)response.StatusCode}): {body}");
        }
    }

    private static string? TryReadId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Sarah Lee" + a@b.com => "Sarah Lee" &lt;a@b.com&gt;. Names are cleaned so they can't break the header.</summary>
    private static string FormatAddress(string email, string? name)
    {
        var cleanEmail = OneLine(email).Trim();
        if (string.IsNullOrWhiteSpace(name)) return cleanEmail;

        var cleanName = new string(OneLine(name).Where(c => c is not ('"' or '<' or '>' or ',' or ';')).ToArray()).Trim();
        return cleanName.Length == 0 ? cleanEmail : $"\"{cleanName}\" <{cleanEmail}>";
    }

    /// <summary>"abc@x.com" or "&lt;abc@x.com&gt;" => "&lt;abc@x.com&gt;" (also handles several ids separated by spaces).</summary>
    private static string Bracket(string ids) =>
        string.Join(' ', OneLine(ids)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => $"<{id.Trim('<', '>')}>"));

    private static string OneLine(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private static string HtmlToText(string html)
    {
        var text = BreakTags.Replace(html, "\n");
        text = AnyTag.Replace(text, string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }
}