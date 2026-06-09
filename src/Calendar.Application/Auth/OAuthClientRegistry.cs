namespace Calendar.Application.Auth;

/// <summary>
/// Resolves the host's OAuth client registration for a provider plugin (PLUGIN-HOST.md §6.3). The client id /
/// secret / redirect URI are HOST configuration — never part of the plugin or its manifest — because the host
/// is the OAuth client. Returns null when no client is configured for the plugin (then OAuth connect is
/// unavailable until the operator registers one).
/// </summary>
public interface IOAuthClientRegistry
{
    /// <summary>
    /// The client registration for <paramref name="pluginId"/>, or null when no ClientId is configured.
    /// <paramref name="fallbackRedirectUri"/> fills in when no RedirectUri is configured (the connect endpoint
    /// passes its own callback URL); token refresh doesn't use the redirect, so runtime callers can omit it.
    /// </summary>
    OAuthClient? Resolve(string pluginId, string? fallbackRedirectUri = null);
}
