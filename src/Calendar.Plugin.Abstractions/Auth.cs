namespace Calendar.Plugin.Abstractions;

/// <summary>Authentication schemes the host can run on a plugin's behalf (PLUGINS.md §6).</summary>
public enum AuthScheme
{
    /// <summary>No credential (public ICS feeds, self-hosted geo). The plugin still gets no raw network beyond the allowlist.</summary>
    None,

    /// <summary>API key / bearer token injected as a header or query param per <see cref="AuthSpec.In"/>/<see cref="AuthSpec.Name"/>/<see cref="AuthSpec.Format"/>.</summary>
    ApiKey,

    /// <summary>HTTP Basic over TLS (username + password).</summary>
    Basic,

    /// <summary>Provider app-specific password over Basic (iCloud/Fastmail/Nextcloud CalDAV).</summary>
    AppPassword,

    /// <summary>OAuth 2.0 Authorization Code + PKCE (Google, Microsoft).</summary>
    OAuth2Pkce,

    /// <summary>OAuth 2.0 Client Credentials (machine-to-machine).</summary>
    OAuth2ClientCredentials
}

/// <summary>
/// The plugin's sole authentication entry point. Never exposes client secrets, refresh tokens, or the raw
/// vaulted access token — only a scoped, short-lived <see cref="AuthHandle"/> the plugin applies per call.
/// </summary>
public interface IAuthBroker
{
    /// <summary>
    /// Get a fresh handle for the requested <paramref name="scopes"/> (a subset of the manifest's declared
    /// scopes). The broker refreshes silently; the handle may be valid for only minutes. Callers should
    /// fetch a handle immediately before use and not cache it across calls.
    /// </summary>
    Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct);

    /// <summary>
    /// Convenience: apply the current credential to <paramref name="request"/> per the manifest's
    /// <see cref="AuthScheme"/> (sets Authorization/Basic/apikey header or query param). Preferred over
    /// reading the raw token, so the plugin never handles the secret material directly.
    /// </summary>
    Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct);
}

/// <summary>
/// A scoped, short-lived authentication handle. For bearer/oauth schemes <see cref="Token"/> is an opaque
/// access token the broker minted and will rotate; it is NOT a stored secret and must not be persisted.
/// For <see cref="AuthScheme.None"/> the handle is <see cref="AuthHandle.Empty"/>.
/// </summary>
public sealed record AuthHandle(
    AuthScheme Scheme,
    string? Token,                              // bearer/apikey value; null for None/Basic
    string? Username,                           // Basic / app-password
    string? Password,                           // Basic / app-password (the app-specific password, never the account password)
    DateTimeOffset? ExpiresAt)                  // when the handle stops being valid; refetch after this
{
    public static readonly AuthHandle Empty = new(AuthScheme.None, null, null, null, null);
}
