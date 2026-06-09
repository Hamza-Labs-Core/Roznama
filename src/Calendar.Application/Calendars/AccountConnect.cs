using Calendar.Application.Auth;

namespace Calendar.Application.Calendars;

/// <summary>
/// A generic connect request (API.md "accounts — connect flow"): which plugin, an optional display name, and
/// the plugin's manifest-schema config values. Credential-bearing keys (<c>password</c>, <c>apiKey</c>) are
/// lifted into the vault and never stored in the account config.
/// </summary>
public sealed record ConnectAccountRequest(
    string PluginId,
    string? DisplayName,
    IReadOnlyDictionary<string, string>? Config);

/// <summary>
/// Outcome of <see cref="IAccountConnectService.BeginConnectAsync"/>: either the account is connected
/// (<see cref="Challenge"/> null) or the caller must redirect the user to <see cref="Challenge"/> and the
/// account sits in <c>NeedsAuth</c> until the OAuth callback completes it.
/// </summary>
public sealed record ConnectAccountOutcome(Guid AccountId, AuthChallenge? Challenge);

/// <summary>
/// Connects accounts for ANY plugin by running the manifest's declared auth scheme (PLUGIN-HOST.md §6):
/// scheme-none plugins connect directly; Basic/AppPassword/ApiKey vault the credential then connect;
/// OAuth2 PKCE returns an <see cref="AuthChallenge"/> and finishes in <see cref="CompleteOAuthAsync"/>.
/// </summary>
public interface IAccountConnectService
{
    /// <summary>Start (and for non-interactive schemes, finish) a connect. Throws <see cref="ArgumentException"/>
    /// on missing config values and <see cref="InvalidOperationException"/> on unknown plugin / missing OAuth client.</summary>
    Task<ConnectAccountOutcome> BeginConnectAsync(
        ConnectAccountRequest request, string fallbackRedirectUri, CancellationToken ct);

    /// <summary>Finish an OAuth connect from the provider callback: exchange the code, vault the refresh
    /// token, flip the account to Connected, and kick an initial sync (failures left to the scheduler).</summary>
    Task<Guid> CompleteOAuthAsync(string state, string code, CancellationToken ct);

    /// <summary>The user denied (or the provider errored): drop the pending authorization and its placeholder account.</summary>
    Task CancelOAuthAsync(string state, CancellationToken ct);
}
