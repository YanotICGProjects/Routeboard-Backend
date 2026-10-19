using System.Text.RegularExpressions;

namespace POSShopTicketing.Application.Common.Mailboxes;

/// <summary>
/// The workspace alias becomes the LOCAL PART of the tenant's email
/// address (alias@support.routeboard.online), so it must be a safe,
/// predictable string. Use this in RegisterTenantCommand (and in your
/// GET /auth/slug-available endpoint) so the frontend and backend agree.
/// </summary>
public static class WorkspaceAliasRules
{
    // 3-63 chars, lowercase letters/digits/hyphens, no hyphen at either end.
    private static readonly Regex Pattern =
        new("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$", RegexOptions.Compiled);

    // Addresses with special meaning in email (RFC 2142 + this app's own).
    // "ticket" matters because replies may use ticket+<id>@... addressing.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "postmaster", "abuse", "hostmaster", "webmaster", "admin", "administrator", "root",
        "security", "mailer-daemon", "noreply", "no-reply", "donotreply", "do-not-reply",
        "support", "billing", "sales", "info", "help", "contact", "ticket", "tickets",
        "www", "mail", "api", "app",   // pos-shop = the platform's own support address
    };

    public static bool TryNormalize(string? input, out string alias, out string? error)
    {
        alias = (input ?? string.Empty).Trim().ToLowerInvariant();

        if (!Pattern.IsMatch(alias))
        {
            error = "Use 3-63 characters: lowercase letters, numbers and hyphens only, " +
                    "and don't start or end with a hyphen.";
            return false;
        }

        if (Reserved.Contains(alias))
        {
            error = $"\"{alias}\" is reserved. Please choose another workspace alias.";
            return false;
        }

        error = null;
        return true;
    }
}