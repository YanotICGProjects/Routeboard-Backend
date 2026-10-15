using System;
using System.Collections.Generic;
using System.Text;

namespace POSShopTicketing.Domain.Entities;

public class SendEmailRequest
{
    public string ToEmail { get; set; } = string.Empty;

    public string? ToName { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    public string? FromEmail { get; set; }

    public string? FromName { get; set; }

    public string? ReplyTo { get; set; }
}
