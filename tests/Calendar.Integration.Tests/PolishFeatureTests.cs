using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 6 polish: reminders fire into the in-app notification log at <c>start − lead</c> (and expire
/// silently when missed), ICS import builds a local read-only snapshot calendar with UID upsert, and the
/// toolbar search matches title/location on visible calendars only.
/// </summary>
public sealed class PolishFeatureTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-polish-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    // ── Reminders ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_due_reminder_fires_one_notification_and_never_fires_twice()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var cal = await SeedCalendarAsync(db);
        // The event starts in 10 minutes; a 30-minute lead means the reminder is already due.
        var ev = await SeedEventAsync(db, cal, "Dentist", DateTimeOffset.UtcNow.AddMinutes(10));

        var service = new ReminderService(db, NullLogger<ReminderService>.Instance);
        var dto = await service.CreateAsync(ev, leadMinutes: 30, CancellationToken.None);
        Assert.NotNull(dto);

        var first = await service.SweepAsync(CancellationToken.None);
        Assert.Equal(1, first.Fired);

        var notification = Assert.Single(await db.Notifications.AsNoTracking().ToListAsync());
        Assert.Equal(NotificationKind.Reminder, notification.Kind);
        Assert.Contains("Dentist", notification.Message);

        var second = await service.SweepAsync(CancellationToken.None);
        Assert.Equal(0, second.Fired);                              // FiredAtUtc set ⇒ no repeat.
        Assert.Single(await db.Notifications.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_not_yet_due_reminder_waits_and_a_missed_one_expires_silently()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var cal = await SeedCalendarAsync(db);
        var future = await SeedEventAsync(db, cal, "Far future", DateTimeOffset.UtcNow.AddHours(5));
        var past = await SeedEventAsync(db, cal, "Already happened", DateTimeOffset.UtcNow.AddHours(-2));

        var service = new ReminderService(db, NullLogger<ReminderService>.Instance);
        await service.CreateAsync(future, leadMinutes: 15, CancellationToken.None);   // due in ~4h45m
        await service.CreateAsync(past, leadMinutes: 15, CancellationToken.None);     // window already missed

        var sweep = await service.SweepAsync(CancellationToken.None);

        Assert.Equal(0, sweep.Fired);
        Assert.Equal(1, sweep.Expired);                             // the past one is closed without noise.
        Assert.Empty(await db.Notifications.AsNoTracking().ToListAsync());

        var reminders = await service.ListAsync(CancellationToken.None);
        Assert.Null(reminders.Single(r => r.EventTitle == "Far future").FiredAtUtc);
        Assert.NotNull(reminders.Single(r => r.EventTitle == "Already happened").FiredAtUtc);
    }

    // ── ICS import ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Importing_an_ics_payload_creates_a_read_only_snapshot_calendar()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var service = NewImportService(db);
        var result = await service.ImportIcsAsync("Conference", """
            BEGIN:VCALENDAR
            X-WR-CALNAME:DevConf 2026
            BEGIN:VEVENT
            UID:talk-1@conf
            SUMMARY:Opening keynote
            LOCATION:Hall A
            DTSTART:20260901T090000Z
            DTEND:20260901T100000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:talk-2@conf
            SUMMARY:Plugin architectures
            DTSTART;VALUE=DATE:20260902
            END:VEVENT
            END:VCALENDAR
            """, CancellationToken.None);

        Assert.Equal(2, result.Events);
        var calendar = await db.Calendars.SingleAsync(c => c.Id == result.CalendarId);
        Assert.True(calendar.IsReadOnly);
        Assert.Equal("Conference", calendar.Name);                  // explicit name wins over X-WR-CALNAME.

        var events = await db.Events.Where(e => e.CalendarId == result.CalendarId).ToListAsync();
        Assert.Equal(2, events.Count);
        var keynote = events.Single(e => e.Title == "Opening keynote");
        Assert.Equal("Hall A", keynote.Location);
        Assert.False(keynote.AllDay);
        Assert.True(events.Single(e => e.Title == "Plugin architectures").AllDay);
    }

    [Fact]
    public async Task Reimporting_under_the_same_name_upserts_by_uid()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var service = NewImportService(db);

        const string v1 = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:x@y
            SUMMARY:Old title
            DTSTART:20260901T090000Z
            END:VEVENT
            END:VCALENDAR
            """;
        const string v2 = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:x@y
            SUMMARY:New title
            DTSTART:20260901T100000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var first = await service.ImportIcsAsync("Mine", v1, CancellationToken.None);
        var second = await service.ImportIcsAsync("Mine", v2, CancellationToken.None);

        Assert.Equal(first.CalendarId, second.CalendarId);          // same snapshot calendar reused.
        var ev = Assert.Single(await db.Events.Where(e => e.CalendarId == first.CalendarId).ToListAsync());
        Assert.Equal("New title", ev.Title);
    }

    [Fact]
    public async Task A_non_ics_payload_is_rejected_with_a_clear_error()
    {
        using var db = NewDb();
        db.Database.Migrate();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewImportService(db).ImportIcsAsync(null, "{ \"not\": \"ics\" }", CancellationToken.None));
    }

    // ── Search ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_matches_title_and_location_but_skips_hidden_calendars()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var visible = await SeedCalendarAsync(db);
        var hidden = await SeedCalendarAsync(db, isVisible: false);

        var when = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        await SeedEventAsync(db, visible, "Team offsite", when);
        await SeedEventAsync(db, visible, "Dinner", when.AddDays(1), location: "Offsite venue");
        await SeedEventAsync(db, visible, "Unrelated", when.AddDays(2));
        await SeedEventAsync(db, hidden, "Secret offsite", when.AddDays(3));

        var results = await new EventSearchService(db).SearchAsync("offsite", 25, CancellationToken.None);

        Assert.Equal(2, results.Count);                             // title hit + location hit; hidden skipped.
        Assert.DoesNotContain(results, r => r.Title == "Secret offsite");
        Assert.True(results[0].StartUtc >= results[1].StartUtc);    // newest-first.
    }

    [Fact]
    public async Task Search_treats_like_wildcards_as_literals()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var cal = await SeedCalendarAsync(db);
        await SeedEventAsync(db, cal, "100% done party", DateTimeOffset.UtcNow.AddDays(1));
        await SeedEventAsync(db, cal, "1000 things", DateTimeOffset.UtcNow.AddDays(2));

        var results = await new EventSearchService(db).SearchAsync("100%", 25, CancellationToken.None);

        var hit = Assert.Single(results);                           // "%" must not match "1000 things".
        Assert.Equal("100% done party", hit.Title);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static ImportService NewImportService(CalendarDbContext db) =>
        new(db, new DedupGrouper(db), new DeviceProvider(db), NullLogger<ImportService>.Instance);

    private static async Task<Guid> SeedCalendarAsync(CalendarDbContext db, bool isVisible = true)
    {
        if (!await db.Plugins.AnyAsync(p => p.Id == "org.unifiedcalendar.ics"))
        {
            db.Plugins.Add(new Domain.Entities.Plugin
            {
                Id = "org.unifiedcalendar.ics", Name = "ICS", Version = "1.0", SdkVersion = "1.x",
                Kind = Plugin.Abstractions.PluginKind.Assembly, Manifest = "{}",
            });
        }
        var account = new Account
        {
            Id = Guid.CreateVersion7(), PluginId = "org.unifiedcalendar.ics", DisplayName = "Test",
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(), AccountId = account.Id, RemoteId = Guid.NewGuid().ToString("N"),
            Name = "Cal", IsVisible = isVisible,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Accounts.Add(account);
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar.Id;
    }

    private static async Task<Guid> SeedEventAsync(
        CalendarDbContext db, Guid calendarId, string title, DateTimeOffset start, string? location = null)
    {
        var ev = new Event
        {
            Id = Guid.CreateVersion7(), CalendarId = calendarId,
            RemoteId = Guid.NewGuid().ToString("N"), Uid = Guid.NewGuid().ToString("N"),
            Title = title, StartUtc = start, EndUtc = start.AddHours(1), Location = location,
            Status = EventStatus.Confirmed, RowVersion = Guid.NewGuid().ToString("N"),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
