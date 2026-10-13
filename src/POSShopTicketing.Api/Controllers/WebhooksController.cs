using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Tickets.Commands.IngestInboundEmail;
using POSShopTicketing.Application.Tickets.Common;
using POSShopTicketing.Infrastructure.Inbound;
using POSShopTicketing.Shared.Wrappers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace POSShopTicketing.Api.Controllers;

/// <summary>
/// The one public, unauthenticated surface in the system. The email
/// provider (SendGrid Inbound Parse or Postmark Inbound) calls it - ONE
/// URL for the whole catch-all domain, not one per mailbox. The workspace
/// is found from the envelope recipient inside the handler.
///
/// Auth: a shared secret, because providers cannot send a bearer token.
///   - Postmark: put it in the URL as basic auth  https://rb:SECRET@api.routeboard.online/api/webhooks/inbound-email/postmark
///   - SendGrid: cannot set headers, so use a query string  .../inbound-email/sendgrid?secret=SECRET
///   - Anything else can use the X-Webhook-Secret header.
/// Always serve over HTTPS. Query strings can end up in proxy logs, so
/// prefer basic auth/header where the provider allows it.
/// </summary>
[AllowAnonymous]
[ApiController]
[Route("api/webhooks")]
[EnableRateLimiting("webhooks")]
[RequestSizeLimit(35_000_000)]          // providers allow up to ~30MB per message
[RequestFormLimits(MultipartBodyLengthLimit = 35_000_000)]
public class WebhooksController : ControllerBase
{
    private readonly ISender _mediator;
    private readonly InboundEmailOptions _options;

    public WebhooksController(ISender mediator, IOptions<InboundEmailOptions> options)
    {
        _mediator = mediator;
        _options = options.Value;
    }

    [HttpPost("inbound-email/resend")]
    public async Task<ActionResult<ApiResponse<IngestInboundEmailResult>>> Resend(
           [FromServices] ResendInboundAdapter adapter,
           [FromServices] IOptions<ResendOptions> resendOptions,
           CancellationToken ct)
    {
        // The signature covers the RAW body, so read it as text before parsing anything.
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(ct);

        var verified = SvixSignature.Verify(
            resendOptions.Value.WebhookSecret,
            Request.Headers["svix-id"].ToString(),
            Request.Headers["svix-timestamp"].ToString(),
            Request.Headers["svix-signature"].ToString(),
            rawBody,
            TimeSpan.FromMinutes(5));

        if (!verified) return Unauthorized();

        ResendWebhookEvent? evt;
        try { evt = JsonSerializer.Deserialize<ResendWebhookEvent>(rawBody); }
        catch (JsonException) { return BadRequest(); }

        // Other event types (if you ever subscribe to more): acknowledge and ignore.
        if (evt?.Type != "email.received" || evt.Data is null)
        {
            return Ok(ApiResponse<IngestInboundEmailResult>.Success(
                IngestInboundEmailResult.Ignored(InboundEmailOutcome.IgnoredInvalid), "Ignored."));
        }

        // An exception here (Resend API down) => HTTP 500 => Resend retries. Safe: nothing saved yet.
        var command = await adapter.BuildCommandAsync(evt.Data, ct);
        return await Send(command, ct);
    }


    // ---------------------------------------------------------------- Postmark
    [HttpPost("inbound-email/postmark")]
    public async Task<ActionResult<ApiResponse<IngestInboundEmailResult>>> Postmark(
        [FromBody] PostmarkInboundPayload p, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in p.Headers ?? new())
        {
            if (!string.IsNullOrWhiteSpace(h.Name) && !headers.ContainsKey(h.Name)) headers[h.Name] = h.Value ?? "";
        }

        // Postmark's own MessageID is THEIR id; the email's real Message-ID is in the headers.
        var recipients = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.OriginalRecipient)) recipients.Add(p.OriginalRecipient!);
        if (!string.IsNullOrWhiteSpace(p.To)) recipients.AddRange(SplitAddresses(p.To!));

        var command = Build(
            recipients, p.From, p.FromName, p.Subject, p.HtmlBody, p.TextBody,
            headers, fallbackMessageId: p.MessageID);

        return await Send(command, ct);
    }

    // ---------------------------------------------------------------- SendGrid
    [HttpPost("inbound-email/sendgrid")]
    public async Task<ActionResult<ApiResponse<IngestInboundEmailResult>>> SendGrid(
        [FromForm] SendGridInboundForm f, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var headers = RawHeaderParser.Parse(f.Headers);

        // "envelope" = {"to":["testworkspace@support.routeboard.online"],"from":"sarah@gmail.com"}
        var recipients = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Envelope))
        {
            try
            {
                using var doc = JsonDocument.Parse(f.Envelope);
                if (doc.RootElement.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.Array)
                {
                    recipients.AddRange(to.EnumerateArray()
                        .Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))!);
                }
            }
            catch (JsonException) { /* fall back to the To header below */ }
        }
        if (recipients.Count == 0 && !string.IsNullOrWhiteSpace(f.To)) recipients.AddRange(SplitAddresses(f.To!));

        var command = Build(recipients, f.From, null, f.Subject, f.Html, f.Text, headers, fallbackMessageId: null);
        return await Send(command, ct);
    }

    // ---------------------------------------------------------------- shared
    private async Task<ActionResult<ApiResponse<IngestInboundEmailResult>>> Send(
        IngestInboundEmailCommand command, CancellationToken ct)
    {
        var result = await _mediator.Send(command, ct);

        // Always 200 for handled/ignored/duplicate so the provider stops retrying.
        // Real crashes still surface as 500 -> the provider retries (and we dedupe).
        return Ok(ApiResponse<IngestInboundEmailResult>.Success(result, result.Outcome switch
        {
            InboundEmailOutcome.TicketCreated => "New ticket created.",
            InboundEmailOutcome.MessageAppended => "Message appended to existing ticket.",
            InboundEmailOutcome.Duplicate => "Already processed.",
            _ => "Ignored."
        }));
    }

    private static IngestInboundEmailCommand Build(
        List<string> recipients, string? from, string? fromName, string? subject, string? html, string? text,
        Dictionary<string, string> headers, string? fallbackMessageId)
    {
        headers.TryGetValue("Message-ID", out var headerMessageId);
        headers.TryGetValue("In-Reply-To", out var inReplyTo);
        headers.TryGetValue("References", out var references);

        var messageId = !string.IsNullOrWhiteSpace(headerMessageId) ? headerMessageId
            : !string.IsNullOrWhiteSpace(fallbackMessageId) ? fallbackMessageId
            : DeterministicId(from, subject, html ?? text);   // same email retried => same id => deduped

        return new IngestInboundEmailCommand
        {
            Recipients = recipients,
            SenderEmail = from ?? string.Empty,
            SenderName = fromName,
            Subject = subject ?? string.Empty,
            BodyHtml = html,
            BodyText = text,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            References = references,
            Headers = headers
        };
    }

    private bool IsAuthorized()
    {
        var expected = _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(expected)) return false;   // not configured => closed

        var offered = new List<string>();

        if (Request.Headers.TryGetValue("X-Webhook-Secret", out var header)) offered.Add(header.ToString());
        if (Request.Query.TryGetValue("secret", out var query)) offered.Add(query.ToString());

        // Basic auth (Postmark): the secret is the password part.
        if (Request.Headers.TryGetValue("Authorization", out var auth) &&
            auth.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.ToString()[6..]));
                var colon = decoded.IndexOf(':');
                if (colon >= 0) offered.Add(decoded[(colon + 1)..]);
            }
            catch (FormatException) { }
        }

        // Hash both sides so the comparison is constant-time AND length-independent.
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return offered.Any(o =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(o)), expectedHash));
    }

    private static IEnumerable<string> SplitAddresses(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string DeterministicId(string? from, string? subject, string? body)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{from}|{subject}|{body}"));
        return $"generated-{Convert.ToHexString(hash)[..32].ToLowerInvariant()}@inbound.routeboard";
    }
}

// Field names follow the providers' documented payloads - verify against
// their current docs when you wire them up.
public class PostmarkInboundPayload
{
    public string? From { get; set; }
    public string? FromName { get; set; }
    public string? To { get; set; }
    public string? OriginalRecipient { get; set; }
    public string? Subject { get; set; }
    public string? MessageID { get; set; }
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public List<PostmarkHeader>? Headers { get; set; }
}

public class PostmarkHeader
{
    public string Name { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public class SendGridInboundForm
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Subject { get; set; }
    public string? Html { get; set; }
    public string? Text { get; set; }
    public string? Headers { get; set; }
    public string? Envelope { get; set; }
}