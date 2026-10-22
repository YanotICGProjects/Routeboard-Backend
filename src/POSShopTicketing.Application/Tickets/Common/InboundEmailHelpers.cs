using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace POSShopTicketing.Application.Tickets.Common;

/// <summary>Bound from appsettings "InboundEmail" section / environment variables.</summary>
public class InboundEmailOptions
{
    /// <summary>Shared secret the email provider must present to call the webhook.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Bodies longer than this are truncated before sanitizing/storing.</summary>
    public int MaxBodyChars { get; set; } = 500_000;

    /// <summary>Spam / auto-reply-loop guard: new tickets one sender may open per hour per workspace.</summary>
    public int MaxNewTicketsPerSenderPerHour { get; set; } = 20;
    /// <summary>Signs the Reply-To address of conversation emails. Any long random string.</summary>
    public string ReplyTokenSecret { get; set; } = string.Empty;
}

public static class InboundEmailAddress
{
    /// <summary>Parses "Sarah Lee &lt;sarah@x.com&gt;" or "sarah@x.com". Returns lowercase address + display name.</summary>
    public static (string Email, string? Name)? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!MailAddress.TryCreate(raw.Trim(), out var parsed)) return null;

        var name = string.IsNullOrWhiteSpace(parsed.DisplayName) ? null : parsed.DisplayName.Trim();
        return (parsed.Address.ToLowerInvariant(), name);
    }

    /// <summary>ticket+abc123@host  ->  ticket@host  (sub-addressing / plus-tag removal).</summary>
    public static string StripSubaddress(string email)
    {
        var at = email.LastIndexOf('@');
        if (at <= 0) return email;

        var local = email[..at];
        var plus = local.IndexOf('+');
        return plus > 0 ? local[..plus] + email[at..] : email;
    }
}

public static class MessageIdHelper
{
    /// <summary>
    /// "&lt;abc@mail.gmail.com&gt;" -> "abc@mail.gmail.com" (lowercased).
    /// IMPORTANT: the code that SENDS replies must store its own outgoing
    /// Message-ID through this same method, or header threading won't match.
    /// </summary>
    //public static string Normalize(string? id) =>
    //    (id ?? string.Empty).Trim().Trim('<', '>').Trim().ToLowerInvariant();

// =====================================================================
//  NEW FILE (and it REPLACES your existing MessageIdHelper class):
//  Application/Tickets/Common/MessageIdAndReplyAddress.cs
//
//  DELETE the old "public static class MessageIdHelper" from the file where it lives today (the helpers file) -
//  this file defines it again, in a stronger form, and two copies would not compile.
//
//  WHY THIS FILE EXISTS - the two real causes of "a reply created a NEW ticket":
//
//   1. THE REFERENCES HEADER ARRIVED AS A STRING THAT LOOKS LIKE A JSON LIST:
//          ["<a3e1959edea3...@support.routeboard.online>","<010201a15e67...@eu-west-1.amazonses.com>"]
//      The old code split it on spaces, found ONE "id" (the whole text), and used that as the thread id, which of
//      course matched no ticket. ExtractAll() pulls the real ids out of ANY of the formats mail apps and providers
//      produce:  "<a> <b>",  "<a>, <b>",  ["<a>","<b>"].
//
//   2. THE EMAIL PROVIDER REWRITES THE MESSAGE-ID OF EVERY EMAIL IT SENDS. Your email went out as
//          Message-ID: <010201a15e643a16-...@eu-west-1.amazonses.com>
//      not as the id our server generated. So an id we store can never be matched against what the customer's mail
//      app later sends back. The fix does not depend on Message-IDs at all: the Reply-To of every conversation
//      email is a SIGNED address,   gs1+gs-000114.9f3a1c2b10@support.routeboard.online
//      Whatever the customer's mail app does, their reply goes to that address, and the signature proves the
//      address was issued by us for that ticket (nobody can guess one for another ticket).
// =====================================================================


    // The id inside angle brackets. Quotes and spaces can never be part of an id, which is what lets us pick ids
    // out of junk such as a JSON list.
    private static readonly Regex Bracketed = new("<([^<>\\s\"']+)>", RegexOptions.Compiled);
    private static readonly Regex Separators = new("[\\s,\\[\\]\"']+", RegexOptions.Compiled);

    /// <summary>
    /// ONE id, normalized: no angle brackets, lowercase. "&lt;abc@x.com&gt;" -> "abc@x.com".
    /// If the input is a LIST (e.g. a References header) the FIRST id in it is returned.
    /// IMPORTANT: the code that stores an id must use this same method, or ids will not match later.
    /// </summary>
    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;

        var match = Bracketed.Match(id);
        var core = match.Success ? match.Groups[1].Value : id;

        return core.Trim().Trim('<', '>', '"', '\'', '[', ']', ',', ' ').ToLowerInvariant();
    }

    /// <summary>
    /// EVERY id in a References / In-Reply-To value, in order, each normalized.
    /// Understands:   "&lt;a&gt; &lt;b&gt;"    "&lt;a&gt;, &lt;b&gt;"    ["&lt;a&gt;","&lt;b&gt;"]    "a b"
    /// </summary>
    public static List<string> ExtractAll(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        var ids = Bracketed.Matches(raw)
            .Select(m => Normalize(m.Groups[1].Value))
            .Where(id => id.Length > 0)
            .ToList();

        if (ids.Count == 0)
        {
            // No angle brackets at all: split on whitespace / commas / quotes / square brackets.
            ids = Separators.Split(raw)
                .Select(Normalize)
                .Where(id => id.Length > 0)
                .ToList();
        }

        return ids.Distinct().ToList();
    }
}

/// <summary>
/// The signed Reply-To address of a conversation email.
///
///     workspace address      gs1@support.routeboard.online
///     ticket                 GS-000114
///     reply address          gs1+gs-000114.9f3a1c2b10@support.routeboard.online
///
/// - Everything after the "+" is "ticket number" + "." + a signature (an HMAC of the workspace id and the ticket
///   number, made with InboundEmail:ReplyTokenSecret). Without the secret nobody can build a valid address for a
///   ticket they do not own, so it is safe to trust without any further check.
/// - Mail sent to it still arrives at the normal workspace mailbox (the inbound handler already ignores the "+tag"
///   when it looks the mailbox up), so no DNS or provider change is needed.
/// </summary>
public static class ReplyAddress
{
    private const int SignatureLength = 10;   // hex characters (40 bits)
    private const int MaxLocalPartLength = 64;

    public static string Build(string mailboxAddress, string ticketNumber, Guid tenantId, string secret)
    {
        var at = mailboxAddress.LastIndexOf('@');
        if (at <= 0 || string.IsNullOrWhiteSpace(secret)) return mailboxAddress;

        var local = mailboxAddress[..at];
        var domain = mailboxAddress[(at + 1)..];
        var number = ticketNumber.Trim().ToLowerInvariant();

        var tagged = $"{local}+{number}.{Sign(tenantId, number, secret)}";

        // Email addresses allow at most 64 characters before the "@". Fall back to the plain address if it is too long.
        return tagged.Length > MaxLocalPartLength ? mailboxAddress : $"{tagged}@{domain}";
    }

    /// <summary>
    /// If this address is a valid signed reply address for the given workspace, returns the ticket number
    /// (upper-case, e.g. "GS-000114"). Anything else - no tag, wrong signature, secret not configured - returns null.
    /// </summary>
    public static string? TryGetTicketNumber(string recipientAddress, Guid tenantId, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(recipientAddress)) return null;

        var at = recipientAddress.LastIndexOf('@');
        if (at <= 0) return null;

        var local = recipientAddress[..at];
        var plus = local.IndexOf('+');
        if (plus < 0) return null;

        var tag = local[(plus + 1)..];            // "gs-000114.9f3a1c2b10"
        var dot = tag.LastIndexOf('.');
        if (dot <= 0) return null;

        var number = tag[..dot].ToLowerInvariant();
        var signature = tag[(dot + 1)..].ToLowerInvariant();

        var expected = Sign(tenantId, number, secret);

        var valid = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected));

        return valid ? number.ToUpperInvariant() : null;
    }

    private static string Sign(Guid tenantId, string ticketNumberLower, string secret)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{tenantId:N}|{ticketNumberLower}"));

        return Convert.ToHexString(mac)[..SignatureLength].ToLowerInvariant();
    }
}




public static class RawHeaderParser
{
    /// <summary>Parses the raw header block SendGrid sends in its "headers" field (handles folded lines).</summary>
    public static Dictionary<string, string> Parse(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        string? name = null;
        var value = new StringBuilder();

        void Flush()
        {
            if (name is not null && !result.ContainsKey(name))
            {
                result[name] = value.ToString().Trim();
            }
        }

        foreach (var line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            // A line starting with space/tab continues the previous header.
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                value.Append(' ').Append(line.Trim());
                continue;
            }

            Flush();
            name = null;
            value.Clear();

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            name = line[..colon].Trim();
            value.Append(line[(colon + 1)..]);
        }

        Flush();
        return result;
    }
}

public static class AutoReplyDetector
{
    /// <summary>
    /// True for out-of-office replies, bounces, mailing-list/bulk mail.
    /// Without this check, your auto-reply + a customer's auto-reply can
    /// email each other forever and create hundreds of tickets.
    /// </summary>
    public static bool IsAutoGenerated(IReadOnlyDictionary<string, string> headers, string senderEmail)
    {
        string? Get(string name) => headers.TryGetValue(name, out var v) ? v : null;

        var autoSubmitted = Get("Auto-Submitted");
        if (!string.IsNullOrWhiteSpace(autoSubmitted) &&
            !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var precedence = Get("Precedence")?.Trim().ToLowerInvariant();
        if (precedence is "bulk" or "junk" or "list" or "auto_reply")
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(Get("X-Autoreply")) || !string.IsNullOrWhiteSpace(Get("X-Autorespond")))
        {
            return true;
        }

        var local = senderEmail.Split('@')[0];
        return local is "mailer-daemon" or "postmaster";
    }
}