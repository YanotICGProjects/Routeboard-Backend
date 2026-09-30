using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using POSShopTicketing.Application.Common.Interfaces;

namespace POSShopTicketing.Infrastructure.Services;

public class AppUrlProvider : IAppUrlProvider
{
    private static readonly HashSet<string> AllowedHosts =
    [
        "localhost:4200",
        "localhost:7163",
        "routeboard.online",
        "staging.routeboard.online"
    ];

    private readonly AppUrlSettings _settings;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AppUrlProvider(
        IOptions<AppUrlSettings> settings,
        IHttpContextAccessor httpContextAccessor)
    {
        _settings = settings.Value;
        _httpContextAccessor = httpContextAccessor;
    }

    public string BuildInviteAcceptUrl(string inviteToken)
    {
        var request = _httpContextAccessor.HttpContext?.Request;

        if (request == null)
        {
            throw new InvalidOperationException(
                "Unable to determine request origin.");
        }

        var host = request.Host.Value;

        if (!AllowedHosts.Contains(host))
        {
            throw new InvalidOperationException(
                $"Host '{host}' is not allowlisted.");
        }

        var origin = $"{request.Scheme}://{host}";

        return $"{origin}/accept-invite?token={Uri.EscapeDataString(inviteToken)}";
    }
}