using System.Collections.Concurrent;
using System.Text.Json;
using Calendar.Application.Auth;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Generic account connect for ANY installed plugin (PLUGIN-HOST.md §6, API.md "accounts — connect flow").
/// The manifest's <see cref="AuthScheme"/> decides the dance:
/// <list type="bullet">
///   <item><c>none</c> — store the config, connect, sync (the ICS path generalized).</item>
///   <item><c>basic/app-password/apikey</c> — seal the credential in the AEAD vault (bound to the new
///     account's id), store the config with the vault handle, connect, sync.</item>
///   <item><c>oauth2-pkce</c> — create the account as <see cref="AccountStatus.NeedsAuth"/>, return the
///     provider redirect; the callback (<see cref="CompleteOAuthAsync"/>) vaults the refresh token and
///     flips it to Connected.</item>
/// </list>
/// The initial sync after connect is best-effort: a failure leaves the account in place for the scheduled
/// sync engine to retry with backoff.
/// </summary>
public sealed class AccountConnectService : IAccountConnectService
{
    private readonly CalendarDbContext _db;
    private readonly ISecretVault _configVault;
    private readonly ITokenVault _tokenVault;
    private readonly IOAuthFlowService _oauthFlow;
    private readonly IOAuthClientRegistry _oauthClients;
    private readonly IPluginRegistry _registry;
    private readonly ICalendarSyncService _sync;
    private readonly DeviceProvider _device;
    private readonly PendingOAuthConnects _pending;
    private readonly ILogger<AccountConnectService> _logger;

    public AccountConnectService(
        CalendarDbContext db, ISecretVault configVault, ITokenVault tokenVault,
        IOAuthFlowService oauthFlow, IOAuthClientRegistry oauthClients, IPluginRegistry registry,
        ICalendarSyncService sync, DeviceProvider device, PendingOAuthConnects pending,
        ILogger<AccountConnectService> logger)
    {
        _db = db;
        _configVault = configVault;
        _tokenVault = tokenVault;
        _oauthFlow = oauthFlow;
        _oauthClients = oauthClients;
        _registry = registry;
        _sync = sync;
        _device = device;
        _pending = pending;
        _logger = logger;
    }

    public async Task<ConnectAccountOutcome> BeginConnectAsync(
        ConnectAccountRequest request, string fallbackRedirectUri, CancellationToken ct)
    {
        if (!_registry.TryGet(request.PluginId, out var registration) || registration.State != PluginState.Running)
            throw new InvalidOperationException($"No running plugin '{request.PluginId}'.");

        var manifest = registration.Manifest;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? manifest.Name : request.DisplayName!;
        var config = request.Config ?? new Dictionary<string, string>();

        switch (manifest.Auth.Scheme)
        {
            case AuthScheme.None:
            case AuthScheme.OAuth2ClientCredentials:    // broker mints from host client config; no user secret
            {
                var accountId = await CreateConnectedAccountAsync(
                    request.PluginId, displayName, ConfigJson(config), ct).ConfigureAwait(false);
                await TryInitialSyncAsync(accountId, ct).ConfigureAwait(false);
                return new ConnectAccountOutcome(accountId, null);
            }

            case AuthScheme.Basic:
            case AuthScheme.AppPassword:
            case AuthScheme.ApiKey:
            {
                var (secretKey, secretKind) = manifest.Auth.Scheme == AuthScheme.ApiKey
                    ? ("apiKey", SecretKind.ApiKey)
                    : ("password", manifest.Auth.Scheme == AuthScheme.Basic ? SecretKind.BasicPassword : SecretKind.AppPassword);
                if (!config.TryGetValue(secretKey, out var secret) || string.IsNullOrWhiteSpace(secret))
                    throw new ArgumentException($"'{secretKey}' is required to connect '{request.PluginId}'.");
                if (secretKind != SecretKind.ApiKey &&
                    (!config.TryGetValue("username", out var user) || string.IsNullOrWhiteSpace(user)))
                    throw new ArgumentException($"'username' is required to connect '{request.PluginId}'.");

                // The credential is AEAD-bound to the account id, so mint the id before sealing.
                var accountId = Guid.CreateVersion7();
                var secretRef = await _tokenVault.StoreAsync(
                    secretKind, secret, Aad(accountId), null, ct).ConfigureAwait(false);

                var safeConfig = config
                    .Where(kv => kv.Key != secretKey)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                safeConfig["secretRef"] = secretRef.ToString();

                await CreateConnectedAccountAsync(
                    request.PluginId, displayName, ConfigJson(safeConfig), ct, accountId).ConfigureAwait(false);
                await TryInitialSyncAsync(accountId, ct).ConfigureAwait(false);
                return new ConnectAccountOutcome(accountId, null);
            }

            case AuthScheme.OAuth2Pkce:
            {
                var oauthClient = _oauthClients.Resolve(request.PluginId, fallbackRedirectUri)
                    ?? throw new InvalidOperationException(
                        $"No OAuth client configured for '{request.PluginId}'. " +
                        $"Set OAuthClients:{request.PluginId}:ClientId (and ClientSecret if confidential).");

                var accountId = Guid.CreateVersion7();
                var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
                await EnsurePluginRowAsync(request.PluginId, ct).ConfigureAwait(false);
                _db.Accounts.Add(new Account
                {
                    Id = accountId,
                    PluginId = request.PluginId,
                    DisplayName = displayName,
                    AuthRef = null,                     // set by the callback once the token is vaulted
                    Priority = await NextPriorityAsync(ct).ConfigureAwait(false),
                    Status = AccountStatus.NeedsAuth,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    DeviceId = deviceId,
                    Lamport = 1,
                });
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                var challenge = _oauthFlow.BeginAuthorization(
                    new CredentialContext(accountId, manifest.Auth, oauthClient), scopes: null);
                _pending.Add(challenge.State, new PendingOAuthConnect(accountId, config));
                return new ConnectAccountOutcome(accountId, challenge);
            }

            default:
                throw new InvalidOperationException(
                    $"Auth scheme '{manifest.Auth.Scheme}' of '{request.PluginId}' has no connect flow.");
        }
    }

    public async Task<Guid> CompleteOAuthAsync(string state, string code, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending))
            throw new InvalidOperationException("Unknown or expired connect state; restart the connect flow.");

        var result = await _oauthFlow.CompleteAuthorizationAsync(state, code, ct).ConfigureAwait(false);

        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == pending.AccountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Pending account {pending.AccountId} no longer exists.");

        var config = pending.Config is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(pending.Config, StringComparer.Ordinal);
        config["secretRef"] = result.SecretRef.ToString();

        account.AuthRef = await _configVault.StoreAsync(SecretKind.FeedUrl, ConfigJson(config), ct).ConfigureAwait(false);
        account.Status = AccountStatus.Connected;
        Stamp(account, await _device.GetDeviceIdAsync(ct).ConfigureAwait(false));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await TryInitialSyncAsync(account.Id, ct).ConfigureAwait(false);
        return account.Id;
    }

    public async Task CancelOAuthAsync(string state, CancellationToken ct)
    {
        if (!_pending.TryRemove(state, out var pending))
            return;

        // The placeholder never connected and never synced — a hard delete leaves no trace.
        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.Id == pending.AccountId && a.Status == AccountStatus.NeedsAuth, ct)
            .ConfigureAwait(false);
        if (account is not null)
        {
            _db.Accounts.Remove(account);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<Guid> CreateConnectedAccountAsync(
        string pluginId, string displayName, string configJson, CancellationToken ct, Guid? id = null)
    {
        await EnsurePluginRowAsync(pluginId, ct).ConfigureAwait(false);
        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);

        var account = new Account
        {
            Id = id ?? Guid.CreateVersion7(),
            PluginId = pluginId,
            DisplayName = displayName,
            AuthRef = await _configVault.StoreAsync(SecretKind.FeedUrl, configJson, ct).ConfigureAwait(false),
            Priority = await NextPriorityAsync(ct).ConfigureAwait(false),
            Status = AccountStatus.Connected,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            Lamport = 1,
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return account.Id;
    }

    /// <summary>Best-effort first sync; a failure leaves the account for the scheduler to retry with backoff.</summary>
    private async Task TryInitialSyncAsync(Guid accountId, CancellationToken ct)
    {
        try
        {
            await _sync.SyncAccountAsync(accountId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex,
                "Initial sync failed for account {AccountId}; the scheduled sync engine will retry.", accountId);
        }
    }

    private async Task<int> NextPriorityAsync(CancellationToken ct) =>
        (await _db.Accounts.Select(a => (int?)a.Priority).MaxAsync(ct).ConfigureAwait(false) ?? -1) + 1;

    private async Task EnsurePluginRowAsync(string pluginId, CancellationToken ct)
    {
        if (await _db.Plugins.AnyAsync(p => p.Id == pluginId, ct).ConfigureAwait(false))
            return;

        var manifest = _registry.TryGet(pluginId, out var reg) ? reg.Manifest : null;
        _db.Plugins.Add(new PluginEntity
        {
            Id = pluginId,
            Name = manifest?.Name ?? pluginId,
            Version = manifest?.Version ?? "1.0.0",
            SdkVersion = manifest?.SdkVersion ?? "1.x",
            Kind = manifest?.Kind ?? PluginKind.Assembly,
            Status = PluginStatus.Installed,
            TrustTier = TrustTier.InBox,
            Capabilities = manifest?.Capabilities.ToList() ?? new List<string>(),
            Manifest = "{}",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string ConfigJson(IReadOnlyDictionary<string, string> config) =>
        JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string Aad(Guid accountId) => $"account:{accountId:N}";

    private static void Stamp(ISyncEntity entity, Guid deviceId)
    {
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        entity.DeviceId = deviceId;
        entity.Lamport++;
    }
}

/// <summary>
/// In-flight OAuth connects keyed by the broker-minted CSRF <c>state</c>. A singleton because the begin and
/// callback requests arrive in different scopes (mirrors <c>OAuthFlowService</c>'s own pending map).
/// </summary>
public sealed class PendingOAuthConnects
{
    private readonly ConcurrentDictionary<string, PendingOAuthConnect> _byState = new(StringComparer.Ordinal);

    public void Add(string state, PendingOAuthConnect pending) => _byState[state] = pending;

    public bool TryRemove(string state, out PendingOAuthConnect pending) =>
        _byState.TryRemove(state, out pending!);
}

/// <summary>The account placeholder + user config captured between begin and callback.</summary>
public sealed record PendingOAuthConnect(Guid AccountId, IReadOnlyDictionary<string, string>? Config);
