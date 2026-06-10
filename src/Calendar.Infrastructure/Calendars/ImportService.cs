using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CalendarEntity = Calendar.Domain.Entities.Calendar;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// One-shot ICS import (ROADMAP Phase 6 polish): an iCalendar payload (exported file, .ics attachment)
/// becomes a local read-only snapshot calendar under the synthetic <c>org.unifiedcalendar.import</c> plugin
/// id — no feed URL, no refresh (live subscriptions go through the ICS plugin instead). Events upsert by
/// UID so re-importing a newer file under the same name updates in place, and the dedup grouper runs so
/// imported copies collapse against synced ones.
/// </summary>
public sealed class ImportService : IImportService
{
    private const string ImportPluginId = "org.unifiedcalendar.import";
    private const string DefaultName = "Imported calendar";

    private readonly CalendarDbContext _db;
    private readonly DedupGrouper _dedup;
    private readonly DeviceProvider _device;
    private readonly ILogger<ImportService> _logger;

    public ImportService(
        CalendarDbContext db, DedupGrouper dedup, DeviceProvider device, ILogger<ImportService> logger)
    {
        _db = db;
        _dedup = dedup;
        _device = device;
        _logger = logger;
    }

    public async Task<ImportResult> ImportIcsAsync(string? name, string ics, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ics))
            throw new ArgumentException("An ICS payload is required.", nameof(ics));

        Ical.Net.Calendar parsed;
        try
        {
            parsed = Ical.Net.Calendar.Load(ics)
                ?? throw new ArgumentException("The payload is not an iCalendar document.", nameof(ics));
        }
        catch (Exception ex) when (ex is not ArgumentException and not OperationCanceledException)
        {
            throw new ArgumentException($"The payload could not be parsed as iCalendar: {ex.Message}", nameof(ics));
        }

        var displayName = string.IsNullOrWhiteSpace(name)
            ? parsed.Properties.Get<string>("X-WR-CALNAME") ?? DefaultName
            : name!;

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var calendar = await GetOrCreateSnapshotCalendarAsync(displayName, deviceId, ct).ConfigureAwait(false);

        var imported = 0;
        foreach (var ce in parsed.Events)
        {
            ct.ThrowIfCancellationRequested();
            if (ce.Start is null)
                continue;   // a VEVENT without DTSTART can't be placed on a grid.

            var uid = string.IsNullOrWhiteSpace(ce.Uid) ? Guid.NewGuid().ToString("N") : ce.Uid;
            var remoteId = ce.RecurrenceId is null ? uid : $"{uid}#{ce.RecurrenceId}";

            var ev = await _db.Events
                .FirstOrDefaultAsync(e => e.CalendarId == calendar.Id && e.RemoteId == remoteId, ct)
                .ConfigureAwait(false);
            if (ev is null)
            {
                ev = new Event { Id = Guid.CreateVersion7(), CalendarId = calendar.Id, RemoteId = remoteId };
                _db.Events.Add(ev);
            }

            // All-day VEVENTs are floating DATE values — AsUtc would shift them through the machine's
            // local zone (off-by-one day east of UTC). Pin them to UTC midnight of the literal date.
            var startUtc = ce.IsAllDay
                ? new DateTimeOffset(DateTime.SpecifyKind(ce.Start.Value.Date, DateTimeKind.Utc), TimeSpan.Zero)
                : new DateTimeOffset(ce.Start.AsUtc, TimeSpan.Zero);
            var endUtc = ce.End is not null
                ? ce.IsAllDay
                    ? new DateTimeOffset(DateTime.SpecifyKind(ce.End.Value.Date, DateTimeKind.Utc), TimeSpan.Zero)
                    : new DateTimeOffset(ce.End.AsUtc, TimeSpan.Zero)
                : ce.IsAllDay ? startUtc.AddDays(1) : startUtc.AddHours(1);

            ev.Uid = uid;
            ev.Title = ce.Summary ?? "(untitled)";
            ev.StartUtc = startUtc;
            ev.EndUtc = endUtc;
            ev.AllDay = ce.IsAllDay;
            ev.StartDate = ce.IsAllDay ? DateOnly.FromDateTime(startUtc.UtcDateTime) : null;
            ev.Rrule = ce.RecurrenceRules is { Count: > 0 } rules ? rules[0].ToString() : null;
            ev.Location = string.IsNullOrWhiteSpace(ce.Location) ? null : ce.Location;
            ev.Status = EventStatus.Confirmed;
            ev.RowVersion = Guid.NewGuid().ToString("N");
            ev.DedupSignature = Domain.Engines.DedupSignatureCalculator.Compute(
                ev.Title, DateOnly.FromDateTime(startUtc.UtcDateTime), ev.AllDay);
            imported++;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _dedup.RegroupAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Imported {Count} events into snapshot calendar '{Name}'.", imported, displayName);
        return new ImportResult(calendar.AccountId, calendar.Id, imported);
    }

    /// <summary>The snapshot calendar for this import name — one account row per imported calendar name.</summary>
    private async Task<CalendarEntity> GetOrCreateSnapshotCalendarAsync(
        string displayName, Guid deviceId, CancellationToken ct)
    {
        if (!await _db.Plugins.AnyAsync(p => p.Id == ImportPluginId, ct).ConfigureAwait(false))
        {
            _db.Plugins.Add(new PluginEntity
            {
                Id = ImportPluginId,
                Name = "ICS import",
                Version = "1.0.0",
                SdkVersion = "1.x",
                Kind = Plugin.Abstractions.PluginKind.Assembly,
                Status = PluginStatus.Installed,
                TrustTier = TrustTier.InBox,
                Capabilities = new List<string>(),
                Manifest = "{}",
                InstalledAtUtc = DateTimeOffset.UtcNow,
            });
        }

        var account = await _db.Accounts
            .FirstOrDefaultAsync(a => a.PluginId == ImportPluginId && a.DisplayName == displayName && !a.IsDeleted, ct)
            .ConfigureAwait(false);
        if (account is null)
        {
            account = new Account
            {
                Id = Guid.CreateVersion7(),
                PluginId = ImportPluginId,
                DisplayName = displayName,
                Priority = (await _db.Accounts.Select(a => (int?)a.Priority).MaxAsync(ct).ConfigureAwait(false) ?? -1) + 1,
                Status = AccountStatus.Connected,
                LastSyncAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DeviceId = deviceId,
                Lamport = 1,
            };
            _db.Accounts.Add(account);
        }

        var calendar = await _db.Calendars
            .FirstOrDefaultAsync(c => c.AccountId == account.Id, ct).ConfigureAwait(false);
        if (calendar is null)
        {
            calendar = new CalendarEntity
            {
                Id = Guid.CreateVersion7(),
                AccountId = account.Id,
                RemoteId = "import",
                Name = displayName,
                IsVisible = true,
                IsReadOnly = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                DeviceId = deviceId,
                Lamport = 1,
            };
            _db.Calendars.Add(calendar);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return calendar;
    }
}
