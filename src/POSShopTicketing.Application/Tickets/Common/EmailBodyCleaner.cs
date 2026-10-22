using System.Net;
using System.Text.RegularExpressions;

namespace POSShopTicketing.Application.Tickets.Common;

/// <summary>
/// NEW FILE: Application/Tickets/Common/EmailBodyCleaner.cs
///
/// Turns an inbound email into what the customer actually WROTE, as plain text:
///
///   BEFORE (what you saw in the conversation)
///     &lt;div dir="auto" style="font-family: Aptos ..."&gt;Because it is fine ,off the stove abeg&lt;/div&gt; ...
///     ... Sent from Outlook for Android ... From: GS1 ... Sent: ... Subject: Re: Rice ... Why are you eating rice
///
///   AFTER
///     Because it is fine ,off the stove abeg
///
/// Three things are removed:
///   1. HTML markup (tags, styles, scripts) - the result is plain text, with line breaks kept.
///   2. The OLD MESSAGES quoted underneath the new one ("On ... wrote:", "From: / Sent: / Subject:" blocks, "> " lines,
///      Gmail / Outlook / Apple Mail / Yahoo quote containers). The ticket's conversation already holds them.
///   3. Mobile signatures such as "Sent from Outlook for Android" / "Sent from my iPhone".
///
/// Safety net: if cleaning would leave NOTHING (for example a reply written between quoted lines), the full text is
/// kept instead, so a customer's message is never lost.
///
/// The result is PLAIN TEXT: show it as text (Angular/React interpolation, not innerHTML) with CSS
/// "white-space: pre-wrap" so the line breaks appear.
/// </summary>
public static class EmailBodyCleaner
{
    // Where a quoted history starts in HTML. Everything from the first marker onward is dropped.
    private static readonly string[] HtmlQuoteMarkers =
    {
        "class=\"gmail_quote", "class='gmail_quote",   // Gmail
        "class=\"yahoo_quoted", "class='yahoo_quoted", // Yahoo
        "id=\"divRplyFwdMsg\"", "id='divRplyFwdMsg'",  // Outlook (desktop / web)
        "id=\"appendonsend\"",                         // Outlook on the web
        "<blockquote"                                  // Apple Mail and most others
    };

    private static readonly Regex HeadStyleScript = new(@"<(head|style|script)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Br = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Hr = new(@"<hr[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockEnd = new(@"</(p|div|li|tr|h[1-6]|table|blockquote)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"<li[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex OriginalMessage = new(@"^-{2,}\s*original message\s*-{2,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OnWrote = new(@"^on .+ wrote:$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"^[_\-]{5,}$", RegexOptions.Compiled);
    private static readonly Regex FromLine = new(@"^from:\s", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Signature = new(@"^(sent from|get outlook for|sent via)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The clean text of an email. Pass the HTML part and/or the plain-text part; HTML is preferred when present.
    /// </summary>
    public static string ToCleanText(string? html, string? text, int maxChars)
    {
        var hasHtml = !string.IsNullOrWhiteSpace(html);

        // "full" = everything the customer sent; "primary" = the same with the quoted history already cut off in HTML.
        var full = hasHtml ? HtmlToText(html!) : NormalizeNewLines(text);
        var primary = hasHtml ? HtmlToText(CutHtmlAtQuote(html!)) : full;

        var cleaned = Tidy(RemoveSignatureLines(RemoveQuotedHistory(primary)));

        if (cleaned.Length == 0)
        {
            // Never lose a message: fall back to everything, quotes included.
            cleaned = Tidy(RemoveSignatureLines(full));
        }

        if (cleaned.Length == 0) cleaned = "(empty message)";

        return cleaned.Length > maxChars ? cleaned[..maxChars] + "\n[Message truncated]" : cleaned;
    }

    // ------------------------------------------------------------------ HTML -> text
    private static string CutHtmlAtQuote(string html)
    {
        var cut = -1;
        foreach (var marker in HtmlQuoteMarkers)
        {
            var index = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (cut < 0 || index < cut)) cut = index;
        }

        return cut > 0 ? html[..cut] : html;
    }

    private static string HtmlToText(string html)
    {
        var s = HeadStyleScript.Replace(html, " ");
        s = Br.Replace(s, "\n");
        s = Hr.Replace(s, "\n________________________________\n");
        s = BlockEnd.Replace(s, "\n");
        s = ListItem.Replace(s, "- ");
        s = AnyTag.Replace(s, string.Empty);                      // every tag still left

        // Decode AFTER the tags are gone, so "&lt;gs1@x.com&gt;" becomes the visible text "<gs1@x.com>".
        s = WebUtility.HtmlDecode(s).Replace('\u00a0', ' ');

        return NormalizeNewLines(s);
    }

    // ------------------------------------------------------------------ quoted history (text)
    private static string RemoveQuotedHistory(string text)
    {
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            // -----Original Message-----
            if (OriginalMessage.IsMatch(line)) return Join(lines, i);

            // On Wed, 21 Oct 2026 at 20:07, GS1 <gs1@...> wrote:      (also when the line is wrapped in two)
            if (OnWrote.IsMatch(line)) return Join(lines, i);
            if (line.StartsWith("on ", StringComparison.OrdinalIgnoreCase) && i + 1 < lines.Length &&
                lines[i + 1].Trim().EndsWith("wrote:", StringComparison.OrdinalIgnoreCase)) return Join(lines, i);

            // ________________  followed by  From: ...        (Outlook)
            if (Separator.IsMatch(line) && NextNonEmptyLine(lines, i).StartsWith("from:", StringComparison.OrdinalIgnoreCase))
            {
                return Join(lines, i);
            }

            // From: ... / Sent: (or Date:) ... / To: (or Subject:) ...      (header block of the old message)
            if (FromLine.IsMatch(line) && LooksLikeHeaderBlock(lines, i)) return Join(lines, i);

            // > quoted line
            if (line.StartsWith('>')) return Join(lines, i);
        }

        return text;
    }

    private static bool LooksLikeHeaderBlock(string[] lines, int fromIndex)
    {
        var window = lines.Skip(fromIndex + 1).Take(6).Select(l => l.Trim().ToLowerInvariant()).ToList();

        return window.Any(l => l.StartsWith("sent:") || l.StartsWith("date:"))
               && window.Any(l => l.StartsWith("subject:") || l.StartsWith("to:"));
    }

    private static string NextNonEmptyLine(string[] lines, int index) =>
        lines.Skip(index + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;

    private static string Join(string[] lines, int count) => string.Join("\n", lines.Take(count));

    // ------------------------------------------------------------------ tidy up
    private static string RemoveSignatureLines(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !Signature.IsMatch(l.Trim())));

    private static string NormalizeNewLines(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Trims every line, collapses runs of blank lines into one, and trims the ends.</summary>
    private static string Tidy(string text)
    {
        var output = new List<string>();
        var previousBlank = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();

            if (line.Length == 0)
            {
                if (output.Count > 0 && !previousBlank) output.Add(string.Empty);
                previousBlank = true;
            }
            else
            {
                output.Add(line);
                previousBlank = false;
            }
        }

        return string.Join("\n", output).Trim();
    }
}