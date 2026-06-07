using System.Text.Json;
using Calendar.Application.Auth;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Builds one account's <see cref="IPluginHost"/> the same way for read (sync) and write (write-back) paths
/// (PLUGIN-HOST.md §2.3, §6). It binds the plugin a <em>real</em> <see cref="IAuthBroker"/> from the
/// <see cref="IAuthBrokerFactory"/>, so credentialed providers (CalDAV Basic/AppPassword) actually
/// authenticate, while <see cref="AuthScheme.None"/> feeds (ICS) keep working through the no-op broker.
/// </summary>
/// <remarks>
/// The account's vault entry (<see cref="Account.AuthRef"/>) holds the plugin's config JSON. For a
/// credentialed account that JSON also carries a <c>secretRef</c> (the <see cref="ITokenVault"/> handle of the
/// stored password) and the <c>username</c>; this helper lifts those into the <see cref="CredentialContext"/>
/// so the broker can read the password under the per-account AEAD associated-data. The plugin only ever sees
/// <see cref="IAuthBroker"/> — never the secret.
/// </remarks>
public sealed class PluginHostServicesFactory
{
    private readonly ISecretVault _vault;
    private readonly IAuthBrokerFactory _authBrokerFactory;
    private readonly IPluginCache _cache;
    private readonly HttpClient _httpClient;
    private readonly ILoggerFactory _loggerFactory;

    public PluginHostServicesFactory(
        ISecretVault vault,
        IAuthBrokerFactory authBrokerFactory,
        IPluginCache cache,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        _vault = vault;
        _authBrokerFactory = authBrokerFactory;
        _cache = cache;
        _httpClient = httpClient;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Construct the host services for <paramref name="account"/> bound to <paramref name="manifest"/>'s declared
    /// <see cref="AuthSpec"/>. The returned host carries the account's config JSON and a per-account broker.
    /// </summary>
    public async Task<IPluginHost> BuildAsync(Account account, PluginManifest manifest, CancellationToken ct)
    {
        var configJson = account.AuthRef is { } authRef
            ? await _vault.ReadAsync(authRef, ct).ConfigureAwait(false) ?? "{}"
            : "{}";

        var (secretRef, username) = ReadCredentialBinding(manifest.Auth.Scheme, configJson);

        var broker = _authBrokerFactory.Create(new CredentialContext(
            account.Id, manifest.Auth, OAuthClient: null, SecretRef: secretRef, Username: username));

        return new PluginHostServices(
            _loggerFactory.CreateLogger($"Plugin.{account.PluginId}"),
            broker,
            _cache,
            () => _httpClient,
            configJson);
    }

    /// <summary>
    /// Extract the vaulted password handle + username a credentialed scheme needs from the account config JSON.
    /// <see cref="AuthScheme.None"/> (ICS) carries neither. Unknown/missing fields degrade to null — the broker
    /// then fails closed if a credential is actually required.
    /// </summary>
    private static (Guid? SecretRef, string? Username) ReadCredentialBinding(AuthScheme scheme, string configJson)
    {
        if (scheme == AuthScheme.None)
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null);

            Guid? secretRef = root.TryGetProperty("secretRef", out var sr)
                && sr.ValueKind == JsonValueKind.String
                && Guid.TryParse(sr.GetString(), out var g)
                ? g
                : null;

            string? username = root.TryGetProperty("username", out var un) && un.ValueKind == JsonValueKind.String
                ? un.GetString()
                : null;

            return (secretRef, username);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
