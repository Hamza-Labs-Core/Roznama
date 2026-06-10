using System.Security.Cryptography;
using System.Text.Json;
using Calendar.Application.Auth;
using Calendar.Application.Calendars;
using Calendar.Application.Cloud;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CalendarEntity = Calendar.Domain.Entities.Calendar;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Cloud;

/// <summary>
/// The E2E-encrypted cloud sync engine (ROADMAP Phase 6, ADR-0003). One round = push then pull:
/// <list type="bullet">
///   <item><b>Push</b> — rows this device authored (sync-quartet <c>DeviceId</c> = ours) mutated after the
///     push cursor are serialized into one change set, sealed with the passphrase-derived key, and appended
///     to the relay space. Applied remote rows keep their origin <c>DeviceId</c>, so they never echo back.</item>
///   <item><b>Pull</b> — blobs after the pull cursor (excluding our own) are opened and applied row-by-row
///     with last-writer-wins ordering: higher <c>Lamport</c> wins, <c>UpdatedAtUtc</c> breaks ties.
///     Tombstones (<c>IsDeleted</c>) replicate; a tombstone for an unknown row is skipped.</item>
/// </list>
/// Synced: Account (metadata only — <c>AuthRef</c> is stripped and arriving accounts land
/// <see cref="AccountStatus.NeedsAuth"/>; tokens NEVER leave the authorizing device), Calendar, Category,
/// Place, FareWatch, Share. Provider event caches are NOT synced — each device re-pulls events through its
/// own credentials.
/// </summary>
public sealed class CloudSyncService : ICloudSyncService
{
    private const string VaultAad = "cloudsync";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CalendarDbContext _db;
    private readonly ITokenVault _vault;
    private readonly IRelayClient _relay;
    private readonly DeviceProvider _device;
    private readonly ILogger<CloudSyncService> _logger;
    private readonly IChangeFeed? _feed;

    public CloudSyncService(
        CalendarDbContext db, ITokenVault vault, IRelayClient relay, DeviceProvider device,
        ILogger<CloudSyncService> logger, IChangeFeed? feed = null)
    {
        _db = db;
        _vault = vault;
        _relay = relay;
        _device = device;
        _logger = logger;
        _feed = feed;
    }

    public async Task<CloudEnableResult> EnableAsync(
        string passphrase, string relayUrl, Guid? spaceId, string? spaceToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("A passphrase is required.", nameof(passphrase));
        if (string.IsNullOrWhiteSpace(relayUrl))
            throw new ArgumentException("A relay URL is required.", nameof(relayUrl));
        if (spaceId is not null && string.IsNullOrWhiteSpace(spaceToken))
            throw new ArgumentException("Joining an existing space requires its token.", nameof(spaceToken));

        string? createdToken = null;
        Guid space;
        string token;
        if (spaceId is { } existing)
        {
            space = existing;
            token = spaceToken!;
        }
        else
        {
            (space, token) = await _relay.CreateSpaceAsync(relayUrl, ct).ConfigureAwait(false);
            createdToken = token;
        }

        var key = CloudCrypto.DeriveKey(passphrase, space);
        var keyRef = await _vault.StoreAsync(
            SecretKind.ApiKey, Convert.ToBase64String(key), VaultAad, null, ct).ConfigureAwait(false);
        var tokenRef = await _vault.StoreAsync(SecretKind.ApiKey, token, VaultAad, null, ct).ConfigureAwait(false);

        // Re-enabling replaces the previous enrollment (and forgets its vaulted key/token).
        await DisableAsync(ct).ConfigureAwait(false);
        _db.CloudSyncConfigs.Add(new CloudSyncConfig
        {
            Id = Guid.CreateVersion7(),
            RelayUrl = relayUrl.TrimEnd('/'),
            SpaceId = space,
            KeySecretRef = keyRef,
            TokenSecretRef = tokenRef,
            LastPulledSeq = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Cloud sync enabled against {RelayUrl} (space {SpaceId}).", relayUrl, space);
        return new CloudEnableResult(space, createdToken);
    }

    public async Task DisableAsync(CancellationToken ct)
    {
        var configs = await _db.CloudSyncConfigs.ToListAsync(ct).ConfigureAwait(false);
        foreach (var config in configs)
        {
            await _vault.RemoveAsync(config.KeySecretRef, ct).ConfigureAwait(false);
            await _vault.RemoveAsync(config.TokenSecretRef, ct).ConfigureAwait(false);
            _db.CloudSyncConfigs.Remove(config);
        }
        if (configs.Count > 0)
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<CloudStatus> GetStatusAsync(CancellationToken ct)
    {
        var config = await _db.CloudSyncConfigs.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return config is null
            ? new CloudStatus(false, null, null, 0, null)
            : new CloudStatus(true, config.RelayUrl, config.SpaceId, config.LastPulledSeq, config.LastSyncAtUtc);
    }

    public async Task<CloudSyncSummary> SyncAsync(CancellationToken ct)
    {
        var config = await _db.CloudSyncConfigs.FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cloud sync is not enabled.");

        var keyEntry = await _vault.ReadAsync(config.KeySecretRef, VaultAad, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The cloud-sync key is missing from the vault; re-enable cloud sync.");
        var tokenEntry = await _vault.ReadAsync(config.TokenSecretRef, VaultAad, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The relay token is missing from the vault; re-enable cloud sync.");
        var key = Convert.FromBase64String(keyEntry.Value);
        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);

        var pushed = await PushAsync(config, key, tokenEntry.Value, deviceId, ct).ConfigureAwait(false);
        var (blobsPulled, applied) = await PullAsync(config, key, tokenEntry.Value, deviceId, ct).ConfigureAwait(false);

        config.LastSyncAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (pushed > 0 || applied > 0)
            _logger.LogInformation(
                "Cloud sync: {Pushed} rows pushed, {Blobs} blobs pulled, {Applied} rows applied.",
                pushed, blobsPulled, applied);
        // Applied user-state (calendar/category visibility, shares…) changes what views render.
        if (applied > 0)
            _feed?.Publish(ChangeEventTypes.EventsChanged, new { source = "cloud", applied });
        return new CloudSyncSummary(pushed, blobsPulled, applied);
    }

    // ── push ──────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> PushAsync(
        CloudSyncConfig config, byte[] key, string token, Guid deviceId, CancellationToken ct)
    {
        var cursor = config.LastPushedAtUtc ?? DateTimeOffset.MinValue;
        var items = new List<ChangeItem>();
        var maxUpdated = cursor;

        await CollectAsync<Account>("account", deviceId, cursor, items, sanitize: a =>
        {
            a.AuthRef = null;                       // credentials never leave the authorizing device.
            return a;
        }, updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);
        await CollectAsync<CalendarEntity>("calendar", deviceId, cursor, items, null,
            updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);
        await CollectAsync<Category>("category", deviceId, cursor, items, null,
            updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);
        await CollectAsync<Place>("place", deviceId, cursor, items, null,
            updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);
        await CollectAsync<FareWatch>("fareWatch", deviceId, cursor, items, null,
            updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);
        await CollectAsync<Share>("share", deviceId, cursor, items, null,
            updated => maxUpdated = Max(maxUpdated, updated), ct).ConfigureAwait(false);

        if (items.Count == 0)
            return 0;

        var changeSet = new ChangeSet(deviceId, items);
        var sealedPayload = CloudCrypto.Encrypt(key, JsonSerializer.SerializeToUtf8Bytes(changeSet, Json));
        await _relay.PushAsync(config.RelayUrl, config.SpaceId, token, deviceId, sealedPayload, ct).ConfigureAwait(false);

        config.LastPushedAtUtc = maxUpdated;
        return items.Count;
    }

    private async Task CollectAsync<T>(
        string type, Guid deviceId, DateTimeOffset cursor, List<ChangeItem> items,
        Func<T, T>? sanitize, Action<DateTimeOffset> observeUpdated, CancellationToken ct)
        where T : class, ISyncEntity
    {
        // EF.Property keeps the predicate translatable even though the members come from ISyncEntity;
        // IgnoreQueryFilters bypasses the global !IsDeleted filter so TOMBSTONES replicate too.
        var rows = await _db.Set<T>().AsNoTracking().IgnoreQueryFilters()
            .Where(e => EF.Property<Guid>(e, nameof(ISyncEntity.DeviceId)) == deviceId
                        && EF.Property<DateTimeOffset>(e, nameof(ISyncEntity.UpdatedAtUtc)) > cursor)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var outgoing = sanitize is null ? row : sanitize(row);
            items.Add(new ChangeItem(type, JsonSerializer.SerializeToElement(outgoing, Json)));
            observeUpdated(row.UpdatedAtUtc);
        }
    }

    // ── pull + apply ──────────────────────────────────────────────────────────────────────────────

    private async Task<(int Blobs, int Applied)> PullAsync(
        CloudSyncConfig config, byte[] key, string token, Guid deviceId, CancellationToken ct)
    {
        var blobs = await _relay.PullAsync(
            config.RelayUrl, config.SpaceId, token, config.LastPulledSeq, deviceId, ct).ConfigureAwait(false);
        if (blobs.Count == 0)
            return (0, 0);

        var applied = 0;
        foreach (var blob in blobs)
        {
            byte[] plaintext;
            try
            {
                plaintext = CloudCrypto.Decrypt(key, blob.Payload);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "A cloud blob could not be decrypted — wrong passphrase for this space?", ex);
            }

            var changeSet = JsonSerializer.Deserialize<ChangeSet>(plaintext, Json);
            if (changeSet is not null)
            {
                foreach (var item in changeSet.Items)
                {
                    if (await ApplyAsync(item, ct).ConfigureAwait(false))
                        applied++;
                }
            }
            config.LastPulledSeq = Math.Max(config.LastPulledSeq, blob.Seq);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (blobs.Count, applied);
    }

    private async Task<bool> ApplyAsync(ChangeItem item, CancellationToken ct) => item.Type switch
    {
        "account" => await ApplyAccountAsync(item.Data, ct).ConfigureAwait(false),
        "calendar" => await ApplyEntityAsync<CalendarEntity>(item.Data, ct).ConfigureAwait(false),
        "category" => await ApplyEntityAsync<Category>(item.Data, ct).ConfigureAwait(false),
        "place" => await ApplyEntityAsync<Place>(item.Data, ct).ConfigureAwait(false),
        "fareWatch" => await ApplyEntityAsync<FareWatch>(item.Data, ct).ConfigureAwait(false),
        "share" => await ApplyEntityAsync<Share>(item.Data, ct).ConfigureAwait(false),
        _ => false,         // unknown type from a newer schema — skipped, never fatal.
    };

    private async Task<bool> ApplyEntityAsync<T>(JsonElement data, CancellationToken ct)
        where T : class, ISyncEntity
    {
        var remote = data.Deserialize<T>(Json);
        if (remote is null)
            return false;

        // IgnoreQueryFilters: a locally-tombstoned row must still be found, or we'd re-insert its Id.
        var id = EntityId(remote);
        var local = await _db.Set<T>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => EF.Property<Guid>(e, "Id") == id, ct).ConfigureAwait(false);
        if (local is null)
        {
            if (remote.IsDeleted)
                return false;                       // tombstone for a row we never had.
            _db.Set<T>().Add(remote);
            return true;
        }

        if (!RemoteWins(remote, local))
            return false;
        _db.Entry(local).CurrentValues.SetValues(remote);
        return true;
    }

    private async Task<bool> ApplyAccountAsync(JsonElement data, CancellationToken ct)
    {
        var remote = data.Deserialize<Account>(Json);
        if (remote is null)
            return false;

        var local = await _db.Accounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == remote.Id, ct).ConfigureAwait(false);
        if (local is null)
        {
            if (remote.IsDeleted)
                return false;
            await EnsurePluginRowAsync(remote.PluginId, ct).ConfigureAwait(false);
            remote.AuthRef = null;                  // this device must authenticate the account itself.
            remote.Status = AccountStatus.NeedsAuth;
            _db.Accounts.Add(remote);
            return true;
        }

        if (!RemoteWins(remote, local))
            return false;

        // Metadata replicates; the credential binding and auth health stay this device's own.
        var keepAuthRef = local.AuthRef;
        var keepStatus = local.Status;
        var keepLastSync = local.LastSyncAtUtc;
        _db.Entry(local).CurrentValues.SetValues(remote);
        local.AuthRef = keepAuthRef;
        local.Status = keepStatus;
        local.LastSyncAtUtc = keepLastSync;
        return true;
    }

    private async Task EnsurePluginRowAsync(string pluginId, CancellationToken ct)
    {
        if (await _db.Plugins.AnyAsync(p => p.Id == pluginId, ct).ConfigureAwait(false))
            return;
        _db.Plugins.Add(new PluginEntity
        {
            Id = pluginId,
            Name = pluginId,
            Version = "0.0.0",
            SdkVersion = "1.x",
            Kind = Plugin.Abstractions.PluginKind.Assembly,
            Status = PluginStatus.Installed,
            TrustTier = TrustTier.InBox,
            Capabilities = new List<string>(),
            Manifest = "{}",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>LWW: higher Lamport wins; wall-clock breaks ties (ISyncEntity contract, DATA-SCHEMA §7).</summary>
    private static bool RemoteWins(ISyncEntity remote, ISyncEntity local) =>
        remote.Lamport > local.Lamport ||
        (remote.Lamport == local.Lamport && remote.UpdatedAtUtc > local.UpdatedAtUtc);

    private static Guid EntityId(object entity) =>
        (Guid)(entity.GetType().GetProperty("Id")?.GetValue(entity)
            ?? throw new InvalidOperationException($"Synced type {entity.GetType().Name} has no Id."));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private sealed record ChangeSet(Guid DeviceId, List<ChangeItem> Items);
    private sealed record ChangeItem(string Type, JsonElement Data);
}
