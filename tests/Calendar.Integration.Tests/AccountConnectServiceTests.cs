using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Calendar.Application.Auth;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Auth;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// Generic account connect (API.md "accounts — connect flow"): the OAuth PKCE dance end-to-end against a stub
/// token endpoint, the app-password (CalDAV-style) direct connect with vaulted credential, and the failure
/// paths (denied consent, missing OAuth client registration).
/// </summary>
public sealed class AccountConnectServiceTests : IDisposable
{
    private const string OAuthPluginId = "test.oauth.provider";
    private const string PasswordPluginId = "test.password.provider";
    private const string Callback = "https://app.test/api/accounts/oauth/callback";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-connect-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task OAuth_connect_round_trips_challenge_callback_and_initial_sync()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var harness = Harness.Create(db, tokenEndpointJson:
            """{"access_token":"AT-1","refresh_token":"RT-1","expires_in":3600,"token_type":"Bearer"}""");

        var outcome = await harness.Connect.BeginConnectAsync(
            new ConnectAccountRequest(OAuthPluginId, "Work Google", null), Callback, CancellationToken.None);

        // Begin: a NeedsAuth placeholder + a PKCE redirect carrying our client and callback.
        var challenge = outcome.Challenge!;
        Assert.Contains("code_challenge_method=S256", challenge.RedirectUrl);
        Assert.Contains("client_id=client-123", challenge.RedirectUrl);
        Assert.Contains(Uri.EscapeDataString(Callback), challenge.RedirectUrl);
        var account = await db.Accounts.FirstAsync(a => a.Id == outcome.AccountId);
        Assert.Equal(AccountStatus.NeedsAuth, account.Status);
        Assert.Null(account.AuthRef);

        // Callback: code exchange (stubbed), refresh token vaulted, account flips Connected, sync runs.
        var accountId = await harness.Connect.CompleteOAuthAsync(challenge.State, "code-xyz", CancellationToken.None);

        Assert.Equal(outcome.AccountId, accountId);
        account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        Assert.Equal(AccountStatus.Connected, account.Status);
        Assert.NotNull(account.AuthRef);

        // The account config carries the vault handle of the refresh token (and never the token itself).
        var configJson = await harness.ConfigVault.ReadAsync(account.AuthRef!.Value, CancellationToken.None);
        Assert.Contains("secretRef", configJson);
        Assert.DoesNotContain("RT-1", configJson);

        var secretRef = System.Text.Json.JsonDocument.Parse(configJson!)
            .RootElement.GetProperty("secretRef").GetGuid();
        var vaulted = await harness.TokenVault.ReadAsync(secretRef, $"account:{accountId:N}", CancellationToken.None);
        Assert.Equal("RT-1", vaulted!.Value);
        Assert.Equal(SecretKind.OAuthRefreshToken, vaulted.Kind);

        // The initial sync ran through the plugin: its remote calendar exists locally.
        Assert.Equal(1, await db.Calendars.CountAsync(c => c.AccountId == accountId));
    }

    [Fact]
    public async Task Denied_consent_cancels_and_removes_the_placeholder_account()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var harness = Harness.Create(db, tokenEndpointJson: "{}");

        var outcome = await harness.Connect.BeginConnectAsync(
            new ConnectAccountRequest(OAuthPluginId, null, null), Callback, CancellationToken.None);
        Assert.Equal(1, await db.Accounts.CountAsync());

        await harness.Connect.CancelOAuthAsync(outcome.Challenge!.State, CancellationToken.None);

        Assert.Equal(0, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task OAuth_connect_without_a_configured_client_fails_with_guidance()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var harness = Harness.Create(db, tokenEndpointJson: "{}", registerOAuthClient: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Connect.BeginConnectAsync(
            new ConnectAccountRequest(OAuthPluginId, null, null), Callback, CancellationToken.None));
        Assert.Contains($"OAuthClients:{OAuthPluginId}:ClientId", ex.Message);
        Assert.Equal(0, await db.Accounts.CountAsync());    // no orphan placeholder
    }

    [Fact]
    public async Task App_password_connect_vaults_the_password_and_syncs_directly()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var harness = Harness.Create(db, tokenEndpointJson: "{}");

        var outcome = await harness.Connect.BeginConnectAsync(
            new ConnectAccountRequest(PasswordPluginId, "iCloud", new Dictionary<string, string>
            {
                ["serverUrl"] = "https://caldav.icloud.com",
                ["username"] = "user@icloud.com",
                ["password"] = "app-specific-pass",
            }), Callback, CancellationToken.None);

        Assert.Null(outcome.Challenge);
        var account = await db.Accounts.FirstAsync(a => a.Id == outcome.AccountId);
        Assert.Equal(AccountStatus.Connected, account.Status);

        // Config keeps serverUrl/username + the vault handle — the password moved into the AEAD vault.
        var configJson = await harness.ConfigVault.ReadAsync(account.AuthRef!.Value, CancellationToken.None);
        Assert.Contains("user@icloud.com", configJson);
        Assert.Contains("secretRef", configJson);
        Assert.DoesNotContain("app-specific-pass", configJson);

        var secretRef = System.Text.Json.JsonDocument.Parse(configJson!)
            .RootElement.GetProperty("secretRef").GetGuid();
        var vaulted = await harness.TokenVault.ReadAsync(secretRef, $"account:{account.Id:N}", CancellationToken.None);
        Assert.Equal("app-specific-pass", vaulted!.Value);
        Assert.Equal(SecretKind.AppPassword, vaulted.Kind);

        Assert.Equal(1, await db.Calendars.CountAsync(c => c.AccountId == account.Id));
    }

    [Fact]
    public async Task App_password_connect_requires_the_password()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var harness = Harness.Create(db, tokenEndpointJson: "{}");

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Connect.BeginConnectAsync(
            new ConnectAccountRequest(PasswordPluginId, null, new Dictionary<string, string>
            {
                ["serverUrl"] = "https://caldav.icloud.com",
                ["username"] = "user@icloud.com",
            }), Callback, CancellationToken.None));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Harness(
        IAccountConnectService Connect, ISecretVault ConfigVault, ITokenVault TokenVault)
    {
        public static Harness Create(CalendarDbContext db, string tokenEndpointJson, bool registerOAuthClient = true)
        {
            var registry = new PluginRegistry();
            Register(registry, new FakeSourcePlugin(OAuthManifest()));
            Register(registry, new FakeSourcePlugin(PasswordManifest()));

            var device = new DeviceProvider(db);
            var configVault = new SecretVault(db);
            var tokenVault = new InMemoryTokenVault();
            var tokenHttp = new HttpClient(new StubTokenHandler(tokenEndpointJson));
            var flow = new OAuthFlowService(tokenVault, () => tokenHttp, NullLogger<OAuthFlowService>.Instance);
            var oauthClients = new FixedOAuthClientRegistry(registerOAuthClient
                ? new OAuthClient("client-123", null, Callback)
                : null);

            // The fake plugins use credentialed schemes, so syncs need the REAL broker factory (vault-backed).
            var brokerFactory = new AuthBrokerFactory(tokenVault, () => tokenHttp, NullLoggerFactory.Instance);
            var hostFactory = new PluginHostServicesFactory(
                configVault, brokerFactory, new InMemoryPluginCache(),
                new HttpClient(new StubTokenHandler("{}")), NullLoggerFactory.Instance, oauthClients);
            var geocodeAggregator = new Calendar.Infrastructure.Aggregation.GeocodeAggregator(
                registry,
                new Calendar.Infrastructure.Aggregation.InMemoryAggregationResultCache(),
                NullLogger<Calendar.Infrastructure.Aggregation.GeocodeAggregator>.Instance);
            var geocode = new GeocodeService(
                db, geocodeAggregator, registry, device, NullLogger<GeocodeService>.Instance);
            var sync = new CalendarSyncService(
                db, registry, hostFactory, new DedupGrouper(db), device, geocode, NullLoggerFactory.Instance);

            var connect = new AccountConnectService(
                db, configVault, tokenVault, flow, oauthClients, registry, sync, device,
                new PendingOAuthConnects(), NullLogger<AccountConnectService>.Instance);
            return new Harness(connect, configVault, tokenVault);
        }

        private static void Register(PluginRegistry registry, FakeSourcePlugin plugin) =>
            registry.Register(new PluginRegistration(
                plugin.Manifest,
                new[] { Capability.CalendarRead },
                PluginState.Running,
                new TestInstance(plugin.Manifest.Id, plugin)));
    }

    private static PluginManifest OAuthManifest() => new(
        OAuthPluginId, "Test OAuth Provider", "1.0.0", "1.x", PluginKind.Assembly,
        new[] { CapabilityIds.CalendarRead }, Publisher: null,
        new AuthSpec(AuthScheme.OAuth2Pkce,
            AuthorizationUrl: "https://idp.test/authorize",
            TokenUrl: "https://idp.test/token",
            Scopes: new[] { "calendar.read" }),
        new NetworkSpec(new[] { "idp.test" }),
        new ConfigSchema("{}", Array.Empty<string>()));

    private static PluginManifest PasswordManifest() => new(
        PasswordPluginId, "Test Password Provider", "1.0.0", "1.x", PluginKind.Assembly,
        new[] { CapabilityIds.CalendarRead }, Publisher: null,
        new AuthSpec(AuthScheme.AppPassword),
        new NetworkSpec(new[] { "caldav.test" }),
        new ConfigSchema("{}", new[] { "serverUrl", "username" }));

    /// <summary>A calendar.read plugin that returns one empty calendar — enough to prove sync ran.</summary>
    private sealed class FakeSourcePlugin : ICalendarSource
    {
        public FakeSourcePlugin(PluginManifest manifest) => Manifest = manifest;
        public PluginManifest Manifest { get; }
        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteCalendar>>(new[]
            {
                new RemoteCalendar("cal-1", "Primary", null, IsReadOnly: false),
            });
        public Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct) =>
            Task.FromResult(new SyncResult(Array.Empty<RemoteEvent>(), Array.Empty<string>(), "tok-1"));
    }

    private sealed class StubTokenHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StubTokenHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FixedOAuthClientRegistry : IOAuthClientRegistry
    {
        private readonly OAuthClient? _client;
        public FixedOAuthClientRegistry(OAuthClient? client) => _client = client;
        public OAuthClient? Resolve(string pluginId, string? fallbackRedirectUri = null) => _client;
    }

    /// <summary>A minimal AEAD-faking vault: enforces the AAD match like the real AES-GCM one.</summary>
    private sealed class InMemoryTokenVault : ITokenVault
    {
        private readonly ConcurrentDictionary<Guid, (SecretKind Kind, string Value, string? Aad, DateTimeOffset? Expires)> _store = new();

        public Task<Guid> StoreAsync(SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            var id = Guid.CreateVersion7();
            _store[id] = (kind, value, aad, expiresAtUtc);
            return Task.FromResult(id);
        }

        public Task UpdateAsync(Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            _store[id] = (kind, value, aad, expiresAtUtc);
            return Task.CompletedTask;
        }

        public Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct)
        {
            if (!_store.TryGetValue(id, out var entry))
                return Task.FromResult<VaultEntry?>(null);
            if (entry.Aad != aad)
                throw new InvalidOperationException("AEAD associated-data mismatch.");
            return Task.FromResult<VaultEntry?>(new VaultEntry(entry.Kind, entry.Value, entry.Expires));
        }

        public Task RemoveAsync(Guid id, CancellationToken ct)
        {
            _store.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
