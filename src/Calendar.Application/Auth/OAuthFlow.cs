namespace Calendar.Application.Auth;

/// <summary>
/// Runs the OAuth 2.0 Authorization-Code + PKCE connect flow on the host's behalf (PLUGIN-HOST.md §6.3).
/// The plugin contributes only manifest fields; this service owns the whole dance — generating the PKCE
/// verifier/challenge and CSRF state, exchanging the code, and vaulting the resulting refresh token. A single
/// <c>GET /accounts/oauth/callback</c> serves every OAuth plugin; the opaque <see cref="AuthChallenge.State"/>
/// routes the callback to its pending authorization.
/// </summary>
public interface IOAuthFlowService
{
    /// <summary>
    /// Phase 1 of connect: mint a PKCE <c>code_verifier</c> + S256 challenge and a CSRF <c>state</c>, stash
    /// the pending authorization, and build the provider authorization URL. The caller redirects the user to
    /// <see cref="AuthChallenge.RedirectUrl"/>.
    /// </summary>
    AuthChallenge BeginAuthorization(CredentialContext context, IReadOnlyList<string>? scopes);

    /// <summary>
    /// Phase 2 of connect: validate <paramref name="state"/>, exchange <paramref name="code"/> at the token
    /// endpoint using the stashed verifier (no client_secret for public PKCE), and seal the refresh token in
    /// the vault. Returns the vault handle to record on the account's <c>AuthRef</c>.
    /// </summary>
    Task<OAuthConnectResult> CompleteAuthorizationAsync(string state, string code, CancellationToken ct);
}

/// <summary>
/// The redirect challenge returned to the UI from <c>POST /accounts</c> (API.md "accounts — connect flow").
/// <see cref="State"/> is opaque, broker-minted, and CSRF-bound; it carries which pending authorization the
/// callback belongs to.
/// </summary>
public sealed record AuthChallenge(string RedirectUrl, string State);

/// <summary>Outcome of a completed OAuth connect: the vault handle to set on the account, plus token expiry.</summary>
public sealed record OAuthConnectResult(Guid SecretRef, DateTimeOffset? AccessTokenExpiresAtUtc);
