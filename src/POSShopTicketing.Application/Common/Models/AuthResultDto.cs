using POSShopTicketing.Domain.Enums;

namespace POSShopTicketing.Application.Common.Models;

/// <summary>Returned by login, refresh, tenant registration, and invite
/// acceptance - always the same shape.</summary>
public record AuthResultDto
{
    public Guid TeamMemberId { get; init; }
    public Guid? TenantId { get; init; }
    public string Email { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;
    public TeamMemberRole Role { get; init; }
    public string WorkSpaceName { get; init; } = string.Empty;
    public string SupportEmail { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public DateTime AccessTokenExpiresAt { get; init; }
    public string RefreshToken { get; init; } = string.Empty;
    public DateTime RefreshTokenExpiresAt { get; init; }
}
