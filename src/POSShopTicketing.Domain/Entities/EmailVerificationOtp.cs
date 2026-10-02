using POSShopTicketing.Domain.Common;

namespace POSShopTicketing.Domain.Entities;

public class EmailVerificationOtp : BaseAuditableEntity
{
    public string Email { get; set; } = string.Empty;

    public string CodeHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public bool IsUsed { get; set; }

    public string VerificationToken { get; set; } = string.Empty;
}