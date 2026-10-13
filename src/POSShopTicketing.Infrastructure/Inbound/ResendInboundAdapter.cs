using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Tickets.Commands.IngestInboundEmail;
using POSShopTicketing.Application.Tickets.Common;

namespace POSShopTicketing.Infrastructure.Inbound;

// =====================================================================
//  Resend sends a small SIGNED "email.received" event (metadata only).
//  The body, headers and attachment files must be fetched from the
//  Resend API. This file turns all of that into the SAME internal
//  IngestInboundEmailCommand that the SendGrid/Postmark endpoints build,
//  so nothing after the adapter knows which provider was used.
//  (If your Api project doesn't reference Infrastructure, move this file
//  into the Api project and change the namespace.)
// =====================================================================

/// <summary>appsettings section "Resend". Keep ApiKey and WebhookSecret in user-secrets / env vars.</summary>
public class ResendOptions
{
    /// <summary>re_xxx - must be allowed to read received emails.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>whsec_xxx - the signing secret shown on the webhook's page in the Resend dashboard.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://api.resend.com/";

    public int MaxAttachments { get; set; } = 10;
    public long MaxAttachmentBytes { get; set; } = 10_000_000;        // per file
    public long MaxTotalAttachmentBytes { get; set; } = 25_000_000;   // per email
}

// ------------------------------------------------------------ signature check
/// <summary>
/// Resend signs webhooks with Svix. Headers: svix-id, svix-timestamp, svix-signature.
/// Signature = base64( HMAC-SHA256( key, "{id}.{timestamp}.{rawBody}" ) ), where key is
/// the base64-decoded part of the secret after "whsec_". The header may hold several
/// space-separated "v1,signature" values. The RAW body must be used - not re-serialized JSON.
/// </summary>
public static class SvixSignature
{
    public static bool Verify(
        string secret, string id, string timestamp, string signatureHeader, string rawBody, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        // Reject old/future timestamps so a captured request can't be replayed later.
        if (!long.TryParse(timestamp, out var seconds)) return false;
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age > tolerance || age < -tolerance) return false;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret.StartsWith("whsec_") ? secret[6..] : secret);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{rawBody}"));

        foreach (var part in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = part.IndexOf(',');
            if (comma < 0 || part[..comma] != "v1") continue;

            try
            {
                var provided = Convert.FromBase64String(part[(comma + 1)..]);
                if (CryptographicOperations.FixedTimeEquals(provided, expected)) return true;
            }
            catch (FormatException) { /* try the next signature */ }
        }

        return false;
    }
}

// ------------------------------------------------------------ JSON models
// Field names follow Resend's documented payloads - verify against their current docs.
public class ResendWebhookEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("data")] public ResendWebhookData? Data { get; set; }
}

public class ResendWebhookData
{
    [JsonPropertyName("email_id")] public string? EmailId { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public List<string>? To { get; set; }
    [JsonPropertyName("cc")] public List<string>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<string>? Bcc { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("message_id")] public string? MessageId { get; set; }
    [JsonPropertyName("attachments")] public List<ResendAttachment>? Attachments { get; set; }
}

public class ResendReceivedEmail
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public List<string>? To { get; set; }
    [JsonPropertyName("cc")] public List<string>? Cc { get; set; }
    [JsonPropertyName("bcc")] public List<string>? Bcc { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("message_id")] public string? MessageId { get; set; }
    [JsonPropertyName("html")] public string? Html { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    // Values are usually strings but can be arrays, so keep them as raw JSON.
    [JsonPropertyName("headers")] public Dictionary<string, JsonElement>? Headers { get; set; }
    [JsonPropertyName("attachments")] public List<ResendAttachment>? Attachments { get; set; }
}

public class ResendAttachment
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("content_type")] public string? ContentType { get; set; }
    [JsonPropertyName("content_disposition")] public string? ContentDisposition { get; set; }
    [JsonPropertyName("content_id")] public string? ContentId { get; set; }
    [JsonPropertyName("size")] public long? Size { get; set; }
    /// <summary>Temporary signed URL - download immediately; re-list attachments for a fresh one.</summary>
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
}

public class ResendAttachmentList
{
    [JsonPropertyName("data")] public List<ResendAttachment>? Data { get; set; }
}

// ------------------------------------------------------------ API client
public class ResendReceivingClient
{
    /// <summary>Named HttpClient that carries the API key (see Program.cs wiring).</summary>
    public const string ApiClientName = "resend-api";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly IHttpClientFactory _factory;

    public ResendReceivingClient(IHttpClientFactory factory) => _factory = factory;

    public async Task<ResendReceivedEmail> GetEmailAsync(string emailId, CancellationToken ct)
    {
        var http = _factory.CreateClient(ApiClientName);
        using var response = await http.GetAsync($"emails/receiving/{Uri.EscapeDataString(emailId)}", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ResendReceivedEmail>(body, Json)
            ?? throw new InvalidOperationException("Resend returned an empty received-email response.");
    }

    public async Task<List<ResendAttachment>> GetAttachmentsAsync(string emailId, CancellationToken ct)
    {
        var http = _factory.CreateClient(ApiClientName);
        using var response = await http.GetAsync($"emails/receiving/{Uri.EscapeDataString(emailId)}/attachments", ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ResendAttachmentList>(body, Json)?.Data ?? new();
    }

    /// <summary>Downloads a file, refusing anything bigger than maxBytes. Returns null if too large/invalid URL.</summary>
    public async Task<byte[]?> DownloadAsync(string url, long maxBytes, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;

        // A plain client WITHOUT the Authorization header: never send your API key to the file host.
        var http = _factory.CreateClient();
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > maxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;   // header lied / chunked response too big
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

// ------------------------------------------------------------ the adapter
public class ResendInboundAdapter
{
    private readonly ResendReceivingClient _client;
    private readonly ResendOptions _options;
    private readonly ILogger<ResendInboundAdapter> _logger;

    public ResendInboundAdapter(
        ResendReceivingClient client, IOptions<ResendOptions> options, ILogger<ResendInboundAdapter> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the full email and builds the provider-neutral command.
    /// Throws if the Resend API call for the email itself fails - the controller then
    /// answers 500 and Resend retries later (safe: nothing has been saved yet).
    /// </summary>
    public async Task<IngestInboundEmailCommand> BuildCommandAsync(ResendWebhookData data, CancellationToken ct)
    {
        var emailId = data.EmailId ?? throw new InvalidOperationException("email.received event has no email_id.");
        var email = await _client.GetEmailAsync(emailId, ct);

        // Case-insensitive so "Message-ID", "message-id" and "Auto-Submitted" all resolve.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in email.Headers ?? new())
        {
            headers[name] = HeaderValue(value);
        }

        headers.TryGetValue("in-reply-to", out var inReplyTo);
        headers.TryGetValue("references", out var references);
        headers.TryGetValue("message-id", out var headerMessageId);

        // Real Message-ID first; deterministic fallback so a retried event still dedupes.
        var messageId = FirstNonEmpty(email.MessageId, data.MessageId, headerMessageId) ?? $"resend-{emailId}";

        // SECURITY NOTE: these are the To/Cc/Bcc fields Resend reports, which may not be the true
        // SMTP envelope recipient (SendGrid's "envelope" and Postmark's OriginalRecipient are).
        // Confirm in Resend's docs/headers whether the real delivered-to address is available,
        // and prefer it if so. Unknown senders only ever become Unverified tickets, which limits impact.
        var recipients = new List<string>();
        recipients.AddRange(data.To ?? new());
        recipients.AddRange(email.To ?? new());
        recipients.AddRange(data.Cc ?? new());
        recipients.AddRange(email.Cc ?? new());
        recipients.AddRange(data.Bcc ?? new());
        recipients.AddRange(email.Bcc ?? new());

        var attachments = await DownloadAttachmentsAsync(emailId, data, email, ct);

        return new IngestInboundEmailCommand
        {
            Recipients = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList(),
            SenderEmail = FirstNonEmpty(email.From, data.From) ?? string.Empty,
            SenderName = null,                       // parsed from "Name <email>" by the handler
            Subject = FirstNonEmpty(email.Subject, data.Subject) ?? string.Empty,
            BodyHtml = email.Html,
            BodyText = email.Text,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            References = references,
            Headers = headers,
            Attachments = attachments
        };
    }

    private async Task<IReadOnlyList<InboundAttachment>> DownloadAttachmentsAsync(
        string emailId, ResendWebhookData data, ResendReceivedEmail email, CancellationToken ct)
    {
        var result = new List<InboundAttachment>();

        var hasAttachments = (data.Attachments?.Count ?? 0) > 0 || (email.Attachments?.Count ?? 0) > 0;
        if (!hasAttachments) return result;

        // Listing gives fresh download_urls. If THIS call fails we throw -> Resend retries the event.
        var list = await _client.GetAttachmentsAsync(emailId, ct);

        long total = 0;
        foreach (var a in list.Take(_options.MaxAttachments))
        {
            if (string.IsNullOrWhiteSpace(a.DownloadUrl)) continue;

            if (a.Size > _options.MaxAttachmentBytes || total + (a.Size ?? 0) > _options.MaxTotalAttachmentBytes)
            {
                _logger.LogWarning("Skipping attachment {File} on {Email}: too large", a.Filename, emailId);
                continue;
            }

            try
            {
                var bytes = await _client.DownloadAsync(a.DownloadUrl, _options.MaxAttachmentBytes, ct);
                if (bytes is null)
                {
                    _logger.LogWarning("Skipping attachment {File} on {Email}: too large or bad URL", a.Filename, emailId);
                    continue;
                }

                // One bad file must not lose the whole customer email, so skip it and carry on.
                total += bytes.Length;
                result.Add(new InboundAttachment(
                    a.Filename ?? "attachment",
                    a.ContentType ?? "application/octet-stream",
                    bytes,
                    a.ContentId,
                    string.Equals(a.ContentDisposition, "inline", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not download attachment {File} on {Email}", a.Filename, emailId);
            }
        }

        return result;
    }

    private static string HeaderValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(HeaderValue)),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.ToString()
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}