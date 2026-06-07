using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Calendar.Application.Auth;
using Calendar.Domain;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// Runs the OAuth 2.0 Authorization-Code + PKCE connect flow (PLUGIN-HOST.md §6.3). It owns the whole dance:
/// generate the verifier/challenge + CSRF state, build the provider authorization URL, exchange the returned
/// code at the token endpoint using the stashed verifier (no client_secret for a public PKCE client), and
/// seal the refresh token in the vault. The plugin contributes only manifest fields and never sees a token.
/// Pending authorizations live in memory keyed by the opaque <c>state</c> so one callback route serves every
/// provider.
/// </summary>
public sealed class OAuthFlowService : IOAuthFlowService
{
    private readonly ITokenVault _vault;
    private readonly Func<HttpClient> _tokenClientFactory;
    private readonly ILogger<OAuthFlowService> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public OAuthFlowService(
        ITokenVault vault,
        Func<HttpClient> tokenClientFactory,
        ILogger<OAuthFlowService> logger,
        TimeProvider? clock = null)
    {
        _vault = vault;
        _tokenClientFactory = tokenClientFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public AuthChallenge BeginAuthorization(CredentialContext context, IReadOnlyList<string>? scopes)
    {
        if (context.Spec.Scheme != AuthScheme.OAuth2Pkce)
            throw new InvalidOperationException("BeginAuthorization is only valid for OAuth2 PKCE.");
        var authUrl = context.Spec.AuthorizationUrl
            ?? throw new InvalidOperationException("OAuth2 PKCE requires AuthSpec.AuthorizationUrl.");
        var client = context.OAuthClient
            ?? throw new InvalidOperationException("OAuth2 PKCE requires an OAuthClient registration.");

        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        var challenge = Pkce.Challenge(verifier);
        var effectiveScopes = scopes ?? context.Spec.Scopes ?? Array.Empty<string>();

        _pending[state] = new Pending(context, verifier, effectiveScopes, _clock.GetUtcNow());

        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", client.ClientId),
            new("redirect_uri", client.RedirectUri),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
            new("state", state),
        };
        if (effectiveScopes.Count > 0)
            query.Add(new("scope", string.Join(' ', effectiveScopes)));
        foreach (var (k, v) in context.Spec.Params ?? new Dictionary<string, string>())
            query.Add(new(k, v)); // e.g. access_type=offline, prompt=consent

        var redirectUrl = AppendQuery(authUrl, query);
        return new AuthChallenge(redirectUrl, state);
    }

    public async Task<OAuthConnectResult> CompleteAuthorizationAsync(string state, string code, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending))
            throw new AuthBrokerException("Unknown or expired OAuth state — possible CSRF; restart the connect flow.");

        var context = pending.Context;
        var client = context.OAuthClient!;
        var tokenUrl = context.Spec.TokenUrl
            ?? throw new InvalidOperationException("OAuth2 PKCE requires AuthSpec.TokenUrl.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", client.RedirectUri),
            new("client_id", client.ClientId),
            new("code_verifier", pending.Verifier),
        };
        if (!string.IsNullOrEmpty(client.ClientSecret))
            form.Add(new("client_secret", client.ClientSecret!)); // confidential client variant

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var http = _tokenClientFactory();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OAuth code exchange failed with status {Status}.", (int)response.StatusCode);
            throw new AuthBrokerException($"OAuth code exchange failed with status {(int)response.StatusCode}.");
        }

        var token = OAuthToken.Parse(body, _clock.GetUtcNow());
        if (string.IsNullOrEmpty(token.RefreshToken))
            throw new AuthBrokerException(
                "Authorization-code exchange returned no refresh_token (request offline access, e.g. access_type=offline).");

        var aad = $"account:{context.AccountId:N}";
        var secretRef = context.SecretRef is { } existing
            ? await Reseal(existing, token.RefreshToken!, aad, token.ExpiresAt, ct).ConfigureAwait(false)
            : await _vault.StoreAsync(
                SecretKind.OAuthRefreshToken, token.RefreshToken!, aad, token.ExpiresAt, ct).ConfigureAwait(false);

        return new OAuthConnectResult(secretRef, token.ExpiresAt);
    }

    private async Task<Guid> Reseal(Guid id, string refresh, string aad, DateTimeOffset? expires, CancellationToken ct)
    {
        await _vault.UpdateAsync(id, SecretKind.OAuthRefreshToken, refresh, aad, expires, ct).ConfigureAwait(false);
        return id;
    }

    private static string AppendQuery(string baseUrl, IEnumerable<KeyValuePair<string, string>> query)
    {
        var encoded = string.Join('&', query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{baseUrl}{separator}{encoded}";
    }

    private readonly record struct Pending(
        CredentialContext Context, string Verifier, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt);
}
