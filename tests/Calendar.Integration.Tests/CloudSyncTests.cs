using System.Collections.Concurrent;
using System.Text;
using Calendar.Application.Auth;
using Calendar.Application.Cloud;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Cloud;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Integration.Tests;

/// <summary>
/// The E2E-encrypted cloud sync node (ROADMAP Phase 6, ADR-0003): two devices share a relay space and
/// converge — rows propagate, LWW orders conflicting writes, tombstones replicate, credentials are stripped
/// (arriving accounts NeedsAuth), the relay only ever holds ciphertext, and a wrong passphrase fails loudly.
/// </summary>
public sealed class CloudSyncTests : IDisposable
{
    private readonly List<string> _dbPaths = new();

    [Fact]
    public async Task Enable_creates_a_space_and_disable_forgets_the_enrollment()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);

        var enabled = await a.Cloud.EnableAsync("hunter2 horse battery", "https://relay.test", null, null, default);
        Assert.NotNull(enabled.SpaceToken);                         // returned exactly once, at creation.
        Assert.True((await a.Cloud.GetStatusAsync(default)).Enabled);

        await a.Cloud.DisableAsync(default);
        Assert.False((await a.Cloud.GetStatusAsync(default)).Enabled);
    }

    [Fact]
    public async Task Two_devices_converge_and_arriving_accounts_need_their_own_auth()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);
        var b = NewDevice(relay);

        var space = await a.Cloud.EnableAsync("pass phrase", "https://relay.test", null, null, default);
        await b.Cloud.EnableAsync("pass phrase", "https://relay.test", space.SpaceId, space.SpaceToken, default);

        var accountId = await SeedAccountAsync(a, "Personal Google", withCredential: true);
        var calendarId = await SeedCalendarAsync(a, accountId, "Family", "#ff6600");

        await a.Cloud.SyncAsync(default);                           // push A's rows
        var pulled = await b.Cloud.SyncAsync(default);              // pull + apply on B

        Assert.True(pulled.Applied >= 2);
        var calendar = await b.Db.Calendars.SingleAsync(c => c.Id == calendarId);
        Assert.Equal("Family", calendar.Name);
        Assert.Equal("#ff6600", calendar.Color);

        var account = await b.Db.Accounts.SingleAsync(x => x.Id == accountId);
        Assert.Equal("Personal Google", account.DisplayName);
        Assert.Null(account.AuthRef);                               // tokens never travel.
        Assert.Equal(AccountStatus.NeedsAuth, account.Status);      // B must authenticate itself.
    }

    [Fact]
    public async Task Edits_flow_back_and_the_higher_lamport_wins()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);
        var b = NewDevice(relay);
        var space = await a.Cloud.EnableAsync("pp", "https://relay.test", null, null, default);
        await b.Cloud.EnableAsync("pp", "https://relay.test", space.SpaceId, space.SpaceToken, default);

        var accountId = await SeedAccountAsync(a, "Acct", withCredential: false);
        var calendarId = await SeedCalendarAsync(a, accountId, "Original", null);
        await a.Cloud.SyncAsync(default);
        await b.Cloud.SyncAsync(default);

        // B renames with a higher Lamport — its write must win on A.
        var onB = await b.Db.Calendars.SingleAsync(c => c.Id == calendarId);
        onB.Name = "Renamed on B";
        onB.Lamport += 1;
        onB.UpdatedAtUtc = DateTimeOffset.UtcNow;
        onB.DeviceId = b.DeviceId;
        await b.Db.SaveChangesAsync();

        await b.Cloud.SyncAsync(default);
        await a.Cloud.SyncAsync(default);

        Assert.Equal("Renamed on B", (await a.Db.Calendars.SingleAsync(c => c.Id == calendarId)).Name);

        // Replaying the ORIGINAL (lower-Lamport) blob must not clobber the newer name on B.
        var second = await b.Cloud.SyncAsync(default);
        Assert.Equal("Renamed on B", (await b.Db.Calendars.SingleAsync(c => c.Id == calendarId)).Name);
        Assert.Equal(0, second.Pushed);                             // A-authored rows never echo from B.
    }

    [Fact]
    public async Task Tombstones_replicate()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);
        var b = NewDevice(relay);
        var space = await a.Cloud.EnableAsync("pp", "https://relay.test", null, null, default);
        await b.Cloud.EnableAsync("pp", "https://relay.test", space.SpaceId, space.SpaceToken, default);

        var category = new Category
        {
            Id = Guid.CreateVersion7(), Name = "Old tag", IsVisible = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = a.DeviceId, Lamport = 1,
        };
        a.Db.Categories.Add(category);
        await a.Db.SaveChangesAsync();
        await a.Cloud.SyncAsync(default);
        await b.Cloud.SyncAsync(default);
        Assert.False((await b.Db.Categories.SingleAsync(c => c.Id == category.Id)).IsDeleted);

        category.IsDeleted = true;
        category.Lamport += 1;
        category.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await a.Db.SaveChangesAsync();
        await a.Cloud.SyncAsync(default);
        await b.Cloud.SyncAsync(default);

        // IgnoreQueryFilters: the global !IsDeleted filter hides the tombstone from normal queries (by design).
        Assert.True((await b.Db.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == category.Id)).IsDeleted);
    }

    [Fact]
    public async Task The_relay_only_ever_sees_ciphertext()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);
        await a.Cloud.EnableAsync("pp", "https://relay.test", null, null, default);

        var accountId = await SeedAccountAsync(a, "Acct", withCredential: false);
        await SeedCalendarAsync(a, accountId, "Secret Family Calendar", null);
        await a.Cloud.SyncAsync(default);

        var blob = Assert.Single(relay.Blobs);
        Assert.False(ContainsSubsequence(blob.Payload, Encoding.UTF8.GetBytes("Secret Family Calendar")));
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    [Fact]
    public async Task A_wrong_passphrase_fails_loudly_instead_of_applying_garbage()
    {
        var relay = new InMemoryRelay();
        var a = NewDevice(relay);
        var b = NewDevice(relay);
        var space = await a.Cloud.EnableAsync("correct horse", "https://relay.test", null, null, default);
        await b.Cloud.EnableAsync("wrong staple", "https://relay.test", space.SpaceId, space.SpaceToken, default);

        var accountId = await SeedAccountAsync(a, "Acct", withCredential: false);
        await SeedCalendarAsync(a, accountId, "Cal", null);
        await a.Cloud.SyncAsync(default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => b.Cloud.SyncAsync(default));
        Assert.Contains("passphrase", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await b.Db.Calendars.ToListAsync());           // nothing half-applied.
    }

    [Fact]
    public async Task The_relay_store_rejects_a_bad_token()
    {
        var device = NewDevice(new InMemoryRelay());
        var store = new RelayStore(device.Db);

        var (spaceId, token) = await store.CreateSpaceAsync(default);
        Assert.NotNull(await store.AppendAsync(spaceId, token, Guid.NewGuid(), new byte[] { 1 }, default));
        Assert.Null(await store.AppendAsync(spaceId, "wrong-token", Guid.NewGuid(), new byte[] { 1 }, default));
        Assert.Null(await store.ReadAsync(spaceId, "wrong-token", 0, null, default));
        Assert.Null(await store.ReadAsync(Guid.NewGuid(), token, 0, null, default));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Device(CalendarDbContext Db, CloudSyncService Cloud, Guid DeviceId);

    private Device NewDevice(InMemoryRelay relay)
    {
        var path = Path.Combine(Path.GetTempPath(), $"calendar-cloud-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        var db = new CalendarDbContext(
            new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();

        var deviceProvider = new DeviceProvider(db);
        var deviceId = deviceProvider.GetDeviceIdAsync(default).GetAwaiter().GetResult();
        var cloud = new CloudSyncService(
            db, new InMemoryTokenVault(), relay, deviceProvider, NullLogger<CloudSyncService>.Instance);
        return new Device(db, cloud, deviceId);
    }

    private static async Task<Guid> SeedAccountAsync(Device device, string name, bool withCredential)
    {
        var pluginId = $"test.plugin.{Guid.NewGuid():N}"[..20];
        device.Db.Plugins.Add(new Domain.Entities.Plugin
        {
            Id = pluginId, Name = "P", Version = "1.0", SdkVersion = "1.x",
            Kind = Plugin.Abstractions.PluginKind.Assembly, Manifest = "{}",
        });

        Guid? authRef = null;
        if (withCredential)
        {
            var secret = new SecretRef
            {
                Id = Guid.CreateVersion7(), Kind = SecretKind.OAuthRefreshToken,
                KeyId = "test", Algorithm = "AES-256-GCM",
                Nonce = new byte[12], Ciphertext = new byte[] { 1, 2, 3 },
                RotatedAtUtc = DateTimeOffset.UtcNow, RowVersion = Guid.NewGuid().ToString("N"),
            };
            device.Db.Secrets.Add(secret);
            authRef = secret.Id;
        }

        var account = new Account
        {
            Id = Guid.CreateVersion7(), PluginId = pluginId, DisplayName = name, AuthRef = authRef,
            Status = AccountStatus.Connected,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = device.DeviceId, Lamport = 1,
        };
        device.Db.Accounts.Add(account);
        await device.Db.SaveChangesAsync();
        return account.Id;
    }

    private static async Task<Guid> SeedCalendarAsync(Device device, Guid accountId, string name, string? color)
    {
        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(), AccountId = accountId, RemoteId = Guid.NewGuid().ToString("N"),
            Name = name, Color = color, IsVisible = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = device.DeviceId, Lamport = 1,
        };
        device.Db.Calendars.Add(calendar);
        await device.Db.SaveChangesAsync();
        return calendar.Id;
    }

    /// <summary>A shared in-memory relay both fake devices talk to; exposes raw blobs for opacity asserts.</summary>
    private sealed class InMemoryRelay : IRelayClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, string> _spaces = new();
        private long _seq;

        public List<(long Seq, Guid SpaceId, Guid DeviceId, byte[] Payload)> Blobs { get; } = new();

        public Task<(Guid SpaceId, string Token)> CreateSpaceAsync(string relayUrl, CancellationToken ct)
        {
            lock (_gate)
            {
                var id = Guid.CreateVersion7();
                var token = Guid.NewGuid().ToString("N");
                _spaces[id] = token;
                return Task.FromResult((id, token));
            }
        }

        public Task<long> PushAsync(string relayUrl, Guid spaceId, string token, Guid deviceId, byte[] payload, CancellationToken ct)
        {
            lock (_gate)
            {
                var seq = ++_seq;
                Blobs.Add((seq, spaceId, deviceId, payload));
                return Task.FromResult(seq);
            }
        }

        public Task<IReadOnlyList<RelayBlobDto>> PullAsync(
            string relayUrl, Guid spaceId, string token, long sinceSeq, Guid excludeDeviceId, CancellationToken ct)
        {
            lock (_gate)
            {
                IReadOnlyList<RelayBlobDto> result = Blobs
                    .Where(b => b.SpaceId == spaceId && b.Seq > sinceSeq && b.DeviceId != excludeDeviceId)
                    .Select(b => new RelayBlobDto(b.Seq, b.DeviceId, b.Payload))
                    .ToList();
                return Task.FromResult(result);
            }
        }
    }

    private sealed class InMemoryTokenVault : ITokenVault
    {
        private readonly ConcurrentDictionary<Guid, (SecretKind Kind, string Value, string? Aad)> _store = new();

        public Task<Guid> StoreAsync(SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            var id = Guid.CreateVersion7();
            _store[id] = (kind, value, aad);
            return Task.FromResult(id);
        }

        public Task UpdateAsync(Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            _store[id] = (kind, value, aad);
            return Task.CompletedTask;
        }

        public Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct) =>
            Task.FromResult(_store.TryGetValue(id, out var entry) && entry.Aad == aad
                ? new VaultEntry(entry.Kind, entry.Value, null)
                : null);

        public Task RemoveAsync(Guid id, CancellationToken ct)
        {
            _store.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        foreach (var path in _dbPaths)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
