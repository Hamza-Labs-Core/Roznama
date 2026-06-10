using System.Net.Http.Headers;
using System.Text;
using Calendar.Application.Auth;
using Calendar.Domain;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// The real auth broker (PLUGIN-HOST.md §6, SDK-CONTRACT.md §3) — the single host component that runs ALL
/// authentication so a plugin never sees a client secret or a stored token. It is bound to one account's
/// <see cref="CredentialContext"/> and handles every <see cref="AuthScheme"/>:
/// <list type="bullet">
///   <item><see cref="AuthScheme.None"/> — <see cref="AuthHandle.Empty"/>.</item>
///   <item><see cref="AuthScheme.ApiKey"/> — header or query injection per <c>AuthSpec.In/Name/Format</c>.</item>
///   <item><see cref="AuthScheme.Basic"/> / <see cref="AuthScheme.AppPassword"/> — HTTP Basic over TLS.</item>
///   <item><see cref="AuthScheme.OAuth2Pkce"/> — bearer minted by silent refresh of the vaulted refresh token.</item>
///   <item><see cref="AuthScheme.OAuth2ClientCredentials"/> — bearer minted by the client_credentials grant.</item>
/// </list>
/// <see cref="GetTokenAsync"/> returns a scoped, short-lived <see cref="AuthHandle"/>; <see cref="ApplyAsync"/>
/// mutates the request directly (preferred — the plugin never touches the secret). The broker NEVER puts a
/// client secret or refresh token into an <see cref="AuthHandle"/>.
/// </summary>
public sealed class AuthBroker : IAuthBroker
{
    // Refresh a little before the real expiry so an in-flight call doesn't race the boundary.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly CredentialContext _ctx;
    private readonly ITokenVault _vault;
    private readonly Func<HttpClient> _tokenClientFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // In-memory cache of the current access token; never persisted (SDK-CONTRACT.md §3).
    private string? _accessToken;
    private DateTimeOffset? _accessExpiresAt;

    public AuthBroker(
        CredentialContext context,
        ITokenVault vault,
        Func<HttpClient> tokenClientFactory,
        ILogger logger,
        TimeProvider? clock = null)
    {
        _ctx = context;
        _vault = vault;
        _tokenClientFactory = tokenClientFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private string Aad => $"account:{_ctx.AccountId:N}";

    public async Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        EnsureScopesPermitted(scopes);
        var scheme = _ctx.Spec.Scheme;
        switch (scheme)
        {
            case AuthScheme.None:
                return AuthHandle.Empty;

            case AuthScheme.ApiKey:
            {
                var key = await ReadSecretAsync(ct).ConfigureAwait(false);
                // Format may wrap the raw key, e.g. "Bearer {token}". The applied value is what a request carries.
                var token = ApplyFormat(_ctx.Spec.Format, key);
                return new AuthHandle(AuthScheme.ApiKey, token, null, null, null);
            }

            case AuthScheme.Basic:
            case AuthScheme.AppPassword:
            {
                var password = await ReadSecretAsync(ct).ConfigureAwait(false);
                return new AuthHandle(scheme, null, _ctx.Username, password, null);
            }

            case AuthScheme.OAuth2Pkce:
            case AuthScheme.OAuth2ClientCredentials:
            {
                var (token, expires) = await GetBearerAsync(ct).ConfigureAwait(false);
                return new AuthHandle(scheme, token, null, null, expires);
            }

            default:
                throw new NotSupportedException($"Unsupported auth scheme '{scheme}'.");
        }
    }

    public async Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        EnsureScopesPermitted(scopes);
        var scheme = _ctx.Spec.Scheme;
        switch (scheme)
        {
            case AuthScheme.None:
                return;

            case AuthScheme.ApiKey:
            {
                var key = await ReadSecretAsync(ct).ConfigureAwait(false);
                ApplyApiKey(request, key);
                return;
            }

            case AuthScheme.Basic:
            case AuthScheme.AppPassword:
            {
                var password = await ReadSecretAsync(ct).ConfigureAwait(false);
                var basic = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{_ctx.Username}:{password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                return;
            }

            case AuthScheme.OAuth2Pkce:
            case AuthScheme.OAuth2ClientCredentials:
            {
                var (token, _) = await GetBearerAsync(ct).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return;
            }

            default:
                throw new NotSupportedException($"Unsupported auth scheme '{scheme}'.");
        }
    }

    // --- apikey placement (AuthSpec.In / Name / Format) ---

    private void ApplyApiKey(HttpRequestMessage request, string rawKey)
    {
        var name = _ctx.Spec.Name ?? "Authorization";
        var value = ApplyFormat(_ctx.Spec.Format, rawKey);
        var placement = _ctx.Spec.In ?? "header";

        if (string.Equals(placement, "query", StringComparison.OrdinalIgnoreCase))
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Cannot inject a query apikey on a request without a URI.");
            request.RequestUri = AppendQuery(uri, name, value);
        }
        else // header (default)
        {
            // "Authorization" must go through the typed header; arbitrary names use the raw header bag.
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Remove("Authorization");
                request.Headers.TryAddWithoutValidation("Authorization", value);
            }
            else
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static string ApplyFormat(string? format, string token) =>
        string.IsNullOrEmpty(format) ? token : format.Replace("{token}", token, StringComparison.Ordinal);

    private static Uri AppendQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var encoded = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? encoded
            : builder.Query.TrimStart('?') + "&" + encoded;
        return builder.Uri;
    }

    // --- OAuth bearer minting (silent refresh + client credentials) ---

    private async Task<(string Token, DateTimeOffset? ExpiresAt)> GetBearerAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_accessToken is { } cached && _accessExpiresAt is { } exp && exp - ExpirySkew > now)
            return (cached, _accessExpiresAt);

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_accessToken is { } stillFresh && _accessExpiresAt is { } e && e - ExpirySkew > now)
                return (stillFresh, _accessExpiresAt);

            var token = _ctx.Spec.Scheme == AuthScheme.OAuth2ClientCredentials
                ? await ClientCredentialsGrantAsync(ct).ConfigureAwait(false)
                : await RefreshGrantAsync(ct).ConfigureAwait(false);

            // PostTokenAsync guarantees a non-empty access_token (it throws otherwise).
            _accessToken = token.AccessToken!;
            _accessExpiresAt = token.ExpiresAt;
            return (token.AccessToken!, token.ExpiresAt);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<OAuthToken> RefreshGrantAsync(CancellationToken ct)
    {
        if (_ctx.SecretRef is not { } secretRef)
            throw new InvalidOperationException("OAuth2 PKCE account has no vaulted refresh token; re-consent required.");

        var entry = await _vault.ReadAsync(secretRef, Aad, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("OAuth2 PKCE refresh token is missing from the vault.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", entry.Value),
        };
        AddClientAuth(form);
        AddScopes(form);

        var token = await PostTokenAsync(form, ct).ConfigureAwait(false);

        // Providers may rotate the refresh token; re-vault it in place so the AuthRef stays stable.
        if (!string.IsNullOrEmpty(token.RefreshToken) && token.RefreshToken != entry.Value)
        {
            await _vault.UpdateAsync(
                secretRef, SecretKind.OAuthRefreshToken, token.RefreshToken!, Aad, token.ExpiresAt, ct)
                .ConfigureAwait(false);
        }
        return token;
    }

    private async Task<OAuthToken> ClientCredentialsGrantAsync(CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
        AddClientAuth(form);
        AddScopes(form);
        return await PostTokenAsync(form, ct).ConfigureAwait(false);
    }

    private void AddClientAuth(List<KeyValuePair<string, string>> form)
    {
        var client = _ctx.OAuthClient
            ?? throw new InvalidOperationException("OAuth flow requires an OAuthClient registration.");
        form.Add(new("client_id", client.ClientId));
        if (!string.IsNullOrEmpty(client.ClientSecret))
            form.Add(new("client_secret", client.ClientSecret!)); // confidential client; never surfaced to plugins
    }

    private void AddScopes(List<KeyValuePair<string, string>> form)
    {
        if (_ctx.Spec.Scopes is { Count: > 0 } scopes)
            form.Add(new("scope", string.Join(' ', scopes)));
    }

    private async Task<OAuthToken> PostTokenAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var tokenUrl = _ctx.Spec.TokenUrl
            ?? throw new InvalidOperationException("OAuth flow requires AuthSpec.TokenUrl.");

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = _tokenClientFactory();
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OAuth token endpoint returned {Status} for account {Account}.",
                (int)response.StatusCode, _ctx.AccountId);
            throw new AuthBrokerException(
                $"OAuth token request failed with status {(int)response.StatusCode}.");
        }

        var parsed = OAuthToken.Parse(body, _clock.GetUtcNow());
        if (string.IsNullOrEmpty(parsed.AccessToken))
            throw new AuthBrokerException("OAuth token response contained no access_token.");
        return parsed;
    }

    private async Task<string> ReadSecretAsync(CancellationToken ct)
    {
        if (_ctx.SecretRef is not { } secretRef)
            throw new InvalidOperationException(
                $"Auth scheme '{_ctx.Spec.Scheme}' requires a stored credential but none is bound.");
        var entry = await _vault.ReadAsync(secretRef, Aad, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bound credential is missing from the vault.");
        return entry.Value;
    }

    private void EnsureScopesPermitted(IReadOnlyList<string>? scopes)
    {
        if (scopes is not { Count: > 0 } requested || _ctx.Spec.Scopes is not { Count: > 0 } declared)
            return;
        foreach (var scope in requested)
        {
            if (!declared.Contains(scope))
                throw new AuthBrokerException(
                    $"Requested scope '{scope}' is not declared in the plugin manifest.");
        }
    }
}

/// <summary>An auth-broker failure that is not a transient HTTP error (bad config, denied scope, bad token response).</summary>
public sealed class AuthBrokerException : Exception
{
    public AuthBrokerException(string message, Exception? inner = null) : base(message, inner) { }
}
