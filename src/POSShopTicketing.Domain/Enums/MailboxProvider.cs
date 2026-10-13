namespace POSShopTicketing.Domain.Enums;

public enum MailboxProvider
{
    SendGridInboundParse = 0,
    PostmarkInbound = 1,
    Other = 2,
    ResendInbound = 3
}
