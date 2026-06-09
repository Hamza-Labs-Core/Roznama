using Calendar.Application.Auth;
using Microsoft.Extensions.Configuration;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// Config-backed <see cref="IOAuthClientRegistry"/>: reads <c>OAuthClients:{pluginId}:ClientId / ClientSecret /
/// RedirectUri</c> (appsettings, env vars, user-secrets). Client secrets are confidential host config — they
/// never live in the plugin manifest or the DB, and the broker never surfaces them to a plugin.
/// </summary>
public sealed class ConfigurationOAuthClientRegistry : IOAuthClientRegistry
{
    private readonly IConfiguration _cfg;

    public ConfigurationOAuthClientRegistry(IConfiguration cfg) => _cfg = cfg;

    public OAuthClient? Resolve(string pluginId, string? fallbackRedirectUri = null)
    {
        var section = _cfg.GetSection($"OAuthClients:{pluginId}");
        var clientId = section["ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            return null;

        // Refresh/client-credentials grants don't use the redirect, so an empty one is fine at runtime;
        // the connect flow always passes its callback URL as the fallback.
        var redirect = section["RedirectUri"];
        if (string.IsNullOrWhiteSpace(redirect))
            redirect = fallbackRedirectUri ?? string.Empty;

        return new OAuthClient(clientId, section["ClientSecret"], redirect);
    }
}
