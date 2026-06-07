using System.Globalization;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Drives one account's sync (ARCHITECTURE §10): get the bound <c>calendar.read</c> plugin from the
/// registry, configure it with the account's feed config, enumerate calendars, run delta sync per calendar,
/// upsert/delete normalized events (idempotent, keyed by (CalendarId, RemoteId)), compute dedup signatures,
/// persist the per-calendar cursor, then re-group duplicates.
/// </summary>
public sealed class CalendarSyncService : ICalendarSyncService
{
    private readonly CalendarDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly ISecretVault _vault;
    private readonly IPluginCache _cache;
    private readonly HttpClient _httpClient;
    private readonly DedupGrouper _dedup;
    private readonly DeviceProvider _device;
    private readonly ILoggerFactory _loggerFactory;

    public CalendarSyncService(
        CalendarDbContext db, IPluginRegistry registry, ISecretVault vault, IPluginCache cache,
        HttpClient httpClient, DedupGrouper dedup, DeviceProvider device, ILoggerFactory loggerFactory)
    {
        _db = db;
        _registry = registry;
        _vault = vault;
        _cache = cache;
        _httpClient = httpClient;
        _dedup = dedup;
        _device = device;
        _loggerFactory = loggerFactory;
    }

    public async Task<SyncSummary> SyncAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = await _db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        if (!_registry.TryGet(account.PluginId, out var registration) ||
            registration.State != PluginState.Running ||
            registration.Instance.Plugin is not ICalendarSource source)
        {
            throw new InvalidOperationException(
                $"No running calendar.read plugin '{account.PluginId}' for account {accountId}.");
        }

        var configJson = account.AuthRef is { } authRef
            ? await _vault.ReadAsync(authRef, ct).ConfigureAwait(false) ?? "{}"
            : "{}";

        var host = new PluginHostServices(
            _loggerFactory.CreateLogger($"Plugin.{account.PluginId}"),
            new NoopAuthBroker(), _cache, () => _httpClient, configJson);
        await source.InitializeAsync(host, ct).ConfigureAwait(false);

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var categories = await _db.Categories.ToListAsync(ct).ConfigureAwait(false);
        var categoriesByName = categories.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        int totalUpserts = 0, totalDeletes = 0;

        var remoteCalendars = await source.ListCalendarsAsync(ct).ConfigureAwait(false);
        foreach (var remote in remoteCalendars)
        {
            var calendar = await UpsertCalendarAsync(account.Id, remote, deviceId, ct).ConfigureAwait(false);

            var syncState = await _db.SyncStates
                .FirstOrDefaultAsync(s => s.AccountId == account.Id && s.CalendarId == calendar.Id, ct)
                .ConfigureAwait(false);

            var result = await source.SyncAsync(remote.RemoteId, syncState?.SyncToken, ct).ConfigureAwait(false);

            foreach (var ev in result.Upserts)
            {
                await UpsertEventAsync(calendar.Id, ev, categoriesByName, deviceId, ct).ConfigureAwait(false);
                totalUpserts++;
            }

            foreach (var remoteId in result.Deletes)
            {
                var existing = await _db.Events
                    .FirstOrDefaultAsync(e => e.CalendarId == calendar.Id && e.RemoteId == remoteId, ct)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    _db.Events.Remove(existing);
                    totalDeletes++;
                }
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await LinkOverridesToMastersAsync(calendar.Id, ct).ConfigureAwait(false);
            await UpsertSyncStateAsync(account.Id, calendar.Id, result.NewSyncToken, ct).ConfigureAwait(false);
        }

        account.LastSyncAtUtc = DateTimeOffset.UtcNow;
        Stamp(account, deviceId);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _dedup.RegroupAsync(ct).ConfigureAwait(false);

        return new SyncSummary(remoteCalendars.Count, totalUpserts, totalDeletes);
    }

    private async Task<CalendarEntity> UpsertCalendarAsync(
        Guid accountId, RemoteCalendar remote, Guid deviceId, CancellationToken ct)
    {
        var calendar = await _db.Calendars
            .FirstOrDefaultAsync(c => c.AccountId == accountId && c.RemoteId == remote.RemoteId, ct)
            .ConfigureAwait(false);

        if (calendar is null)
        {
            calendar = new CalendarEntity
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                RemoteId = remote.RemoteId,
                IsVisible = true,
            };
            _db.Calendars.Add(calendar);
        }

        calendar.Name = remote.Name;
        calendar.Color ??= remote.Color;
        calendar.IsReadOnly = remote.IsReadOnly;
        Stamp(calendar, deviceId);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return calendar;
    }

    private async Task UpsertEventAsync(
        Guid calendarId, RemoteEvent remote, IDictionary<string, Category> categoriesByName,
        Guid deviceId, CancellationToken ct)
    {
        var ev = await _db.Events
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.CalendarId == calendarId && e.RemoteId == remote.RemoteId, ct)
            .ConfigureAwait(false);

        if (ev is null)
        {
            ev = new Event { Id = Guid.CreateVersion7(), CalendarId = calendarId, RemoteId = remote.RemoteId };
            _db.Events.Add(ev);
        }

        ev.Uid = remote.Uid;
        ev.Title = remote.Title;
        ev.StartUtc = remote.StartUtc;
        ev.EndUtc = remote.EndUtc;
        ev.AllDay = remote.AllDay;
        ev.StartDate = remote.AllDay ? DateOnly.FromDateTime(remote.StartUtc.UtcDateTime) : null;
        ev.Rrule = remote.Rrule;
        ev.RecurrenceId = ParseRecurrenceId(remote.RecurrenceId);
        ev.Location = remote.Location;
        ev.Status = remote.Status;
        ev.ETag = remote.ChangeTag;
        ev.RowVersion = Guid.NewGuid().ToString("N");
        ev.DedupSignature = Domain.Engines.DedupSignatureCalculator.Compute(
            remote.Title, DateOnly.FromDateTime(remote.StartUtc.UtcDateTime), remote.AllDay);

        SyncEventCategories(ev, remote.Categories, categoriesByName, deviceId);
    }

    private void SyncEventCategories(
        Event ev, IReadOnlyList<string> names, IDictionary<string, Category> categoriesByName, Guid deviceId)
    {
        ev.Categories.Clear();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!categoriesByName.TryGetValue(name, out var category))
            {
                category = new Category
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    IsVisible = true,
                    IsBuiltIn = false,
                };
                Stamp(category, deviceId);
                _db.Categories.Add(category);
                categoriesByName[name] = category;
            }
            ev.Categories.Add(category);
        }
    }

    private async Task LinkOverridesToMastersAsync(Guid calendarId, CancellationToken ct)
    {
        var overrides = await _db.Events
            .Where(e => e.CalendarId == calendarId && e.RecurrenceId != null && e.MasterId == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (overrides.Count == 0)
            return;

        foreach (var ov in overrides)
        {
            var master = await _db.Events
                .FirstOrDefaultAsync(e => e.CalendarId == calendarId && e.RemoteId == ov.Uid && e.Rrule != null, ct)
                .ConfigureAwait(false);
            if (master is not null)
                ov.MasterId = master.Id;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task UpsertSyncStateAsync(Guid accountId, Guid calendarId, string token, CancellationToken ct)
    {
        var state = await _db.SyncStates
            .FirstOrDefaultAsync(s => s.AccountId == accountId && s.CalendarId == calendarId, ct)
            .ConfigureAwait(false);
        if (state is null)
        {
            state = new SyncState
            {
                Id = Guid.CreateVersion7(),
                AccountId = accountId,
                CalendarId = calendarId,
            };
            _db.SyncStates.Add(state);
        }
        state.SyncToken = token;
        state.LastSyncAtUtc = DateTimeOffset.UtcNow;
        state.State = SyncRunState.Idle;
        state.RowVersion = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static DateTimeOffset? ParseRecurrenceId(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Stamp(ISyncEntity entity, Guid deviceId)
    {
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        entity.DeviceId = deviceId;
        entity.Lamport++;
    }
}
