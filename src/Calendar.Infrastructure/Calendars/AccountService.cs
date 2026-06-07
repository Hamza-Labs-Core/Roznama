using System.Text.Json;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Connects ICS feed accounts (no OAuth — the feed URL is the credential) and lists accounts. The feed
/// config is written to the vault; an initial sync runs immediately so events appear on connect.
/// </summary>
public sealed class AccountService : IAccountService
{
    private const string IcsPluginId = "org.unifiedcalendar.ics";

    private readonly CalendarDbContext _db;
    private readonly ISecretVault _vault;
    private readonly ICalendarSyncService _sync;
    private readonly IPluginRegistry _registry;
    private readonly DeviceProvider _device;

    public AccountService(
        CalendarDbContext db, ISecretVault vault, ICalendarSyncService sync,
        IPluginRegistry registry, DeviceProvider device)
    {
        _db = db;
        _vault = vault;
        _sync = sync;
        _registry = registry;
        _device = device;
    }

    public async Task<Guid> ConnectIcsAsync(
        string feedUrl, string? name, int refreshMinutes, string? forceCategory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            throw new ArgumentException("A feed URL is required.", nameof(feedUrl));

        await EnsurePluginRowAsync(IcsPluginId, ct).ConfigureAwait(false);

        var configJson = JsonSerializer.Serialize(
            new { feedUrl, refreshMinutes = Math.Max(15, refreshMinutes), calendarName = name, forceCategory },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var authRef = await _vault.StoreAsync(SecretKind.FeedUrl, configJson, ct).ConfigureAwait(false);

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var maxPriority = await _db.Accounts.Select(a => (int?)a.Priority).MaxAsync(ct).ConfigureAwait(false) ?? -1;

        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            PluginId = IcsPluginId,
            DisplayName = string.IsNullOrWhiteSpace(name) ? "ICS feed" : name,
            AuthRef = authRef,
            Priority = maxPriority + 1,
            Status = AccountStatus.Connected,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            Lamport = 1,
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _sync.SyncAccountAsync(account.Id, ct).ConfigureAwait(false);
        return account.Id;
    }

    public async Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken ct) =>
        await _db.Accounts
            .OrderBy(a => a.Priority)
            .Select(a => new AccountDto(a.Id, a.PluginId, a.DisplayName, a.Status.ToString(), a.LastSyncAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

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
            Kind = manifest?.Kind ?? Plugin.Abstractions.PluginKind.Assembly,
            Status = PluginStatus.Installed,
            TrustTier = TrustTier.InBox,
            Capabilities = manifest?.Capabilities.ToList() ?? new List<string> { "calendar.read" },
            Manifest = "{}",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
