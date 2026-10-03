namespace POSShopTicketing.Infrastructure.Services;

/// <summary>
/// Bound from the "Smtp" section of appsettings.json. Works with any
/// SMTP-capable provider - Hostinger's own mail hosting, Gmail (with an
/// app password), Outlook, Amazon SES, Mailgun, Postmark, or SendGrid's
/// own SMTP relay if you'd rather keep using it. SendGrid/Postmark are
/// only "recommended" for the INBOUND side (their inbound-parse webhook
/// is what turns an email into a ticket - see Mailbox/WebhooksController)
/// - outbound replies and audit emails just need any working SMTP
/// server, which is what this configures.
/// </summary>
public class SmtpSettings
{
    public const string SectionName = "Smtp";

    /// <summary>Empty (the default) means "not configured" - Infrastructure
    /// DI then falls back to the log-based EmailSender instead of this.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 465;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseStartTls { get; set; } = false;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "RouteBoard";
}
