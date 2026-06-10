using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Auth;

/// <summary>
/// The per-account credential context the host binds a broker to (PLUGIN-HOST.md §6.1). It pairs the
/// plugin's declared <see cref="AuthSpec"/> with the OAuth client registration and the vault handles that
/// hold the account's stored credential. The plugin never sees this — it only ever touches
/// <see cref="IAuthBroker"/>. The broker reads it to run the scheme and to know where its tokens live.
/// </summary>
/// <param name="AccountId">
/// Account this credential belongs to. Used as the AEAD associated-data so a ciphertext can't be swapped
/// between accounts, and as the cache key for the in-flight access token.
/// </param>
/// <param name="Spec">The manifest's <see cref="AuthSpec"/> — scheme, endpoints, apikey placement, scopes.</param>
/// <param name="OAuthClient">
/// OAuth client registration (client_id, optional client_secret, redirect_uri). Confidential — the broker
/// uses it to mint/refresh tokens and NEVER surfaces it through <see cref="AuthHandle"/>. Null for non-OAuth
/// schemes.
/// </param>
/// <param name="SecretRef">
/// Vault handle for the long-lived credential: the OAuth refresh token, or the API key / Basic-password /
/// app-password secret. Null until the account has connected (or for <see cref="AuthScheme.None"/>).
/// </param>
/// <param name="Username">
/// Username for <see cref="AuthScheme.Basic"/> / <see cref="AuthScheme.AppPassword"/>; the password sits in
/// the vault under <see cref="SecretRef"/>. Null otherwise.
/// </param>
public sealed record CredentialContext(
    Guid AccountId,
    AuthSpec Spec,
    OAuthClient? OAuthClient = null,
    Guid? SecretRef = null,
    string? Username = null);

/// <summary>
/// OAuth client registration the host holds for a provider (PLUGIN-HOST.md §6.3). These are CONFIDENTIAL:
/// the broker uses them to run the PKCE / client-credentials grant but never returns them to a plugin. For
/// public PKCE clients <see cref="ClientSecret"/> is null (the code_verifier is the proof).
/// </summary>
public sealed record OAuthClient(
    string ClientId,
    string? ClientSecret,
    string RedirectUri);
