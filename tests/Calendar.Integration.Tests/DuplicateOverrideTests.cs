using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Integration.Tests;

/// <summary>
/// The duplicate merge/split overrides (ARCHITECTURE §12; the Phase 1 follow-up): SetCanonical beats
/// account priority, NeverMerge splits a copy out of automatic grouping, ForceMerge unions across
/// signatures, and tombstoning an override restores the automatic answer — all idempotent across re-runs.
/// </summary>
public sealed class DuplicateOverrideTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-dup-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Baseline_auto_grouping_picks_the_highest_priority_canonical()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var world = await SeedTwoCopiesAsync(db);

        await new DedupGrouper(db).RegroupAsync(CancellationToken.None);

        var group = Assert.Single(await db.DuplicateGroups.ToListAsync());
        Assert.Equal(world.HighPriorityEventId, group.CanonicalEventId);
    }

    [Fact]
    public async Task SetCanonical_beats_account_priority_and_undo_restores_it()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var world = await SeedTwoCopiesAsync(db);
        var service = NewService(db);
        await new DedupGrouper(db).RegroupAsync(CancellationToken.None);

        var dto = await service.CreateOverrideAsync(
            OverrideKind.SetCanonical, new[] { "uid-low" }, "prefer work copy", CancellationToken.None);

        var group = Assert.Single(await db.DuplicateGroups.ToListAsync());
        Assert.Equal(world.LowPriorityEventId, group.CanonicalEventId);   // the override wins.

        Assert.True(await service.RemoveOverrideAsync(dto.Id, CancellationToken.None));
        group = Assert.Single(await db.DuplicateGroups.ToListAsync());
        Assert.Equal(world.HighPriorityEventId, group.CanonicalEventId);  // automatic answer restored.
    }

    [Fact]
    public async Task NeverMerge_splits_the_copy_out_and_dissolves_a_pair_group()
    {
        using var db = NewDb();
        db.Database.Migrate();
        await SeedTwoCopiesAsync(db);
        var service = NewService(db);
        await new DedupGrouper(db).RegroupAsync(CancellationToken.None);
        Assert.Single(await db.DuplicateGroups.ToListAsync());

        await service.CreateOverrideAsync(
            OverrideKind.NeverMerge, new[] { "uid-low" }, null, CancellationToken.None);

        // One copy left ⇒ no duplicate ⇒ the group dissolves and both events render.
        Assert.Empty(await db.DuplicateGroups.ToListAsync());
        Assert.All(await db.Events.ToListAsync(), e => Assert.Null(e.DuplicateGroupId));
    }

    [Fact]
    public async Task ForceMerge_unions_events_across_different_signatures()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var world = await SeedTwoCopiesAsync(db);
        // A third event with a DIFFERENT signature (a retitled copy the hash missed).
        var third = await SeedEventAsync(db, world.HighPriorityCalendarId, "NYE Party 🎉", "uid-retitled", "sig-other");
        var service = NewService(db);

        await service.CreateOverrideAsync(
            OverrideKind.ForceMerge, new[] { "uid-high", "uid-retitled" }, null, CancellationToken.None);

        var groups = await db.DuplicateGroups.ToListAsync();
        var forced = Assert.Single(groups, g => g.Signature.StartsWith("override:"));
        var members = await db.Events.Where(e => e.DuplicateGroupId == forced.Id).ToListAsync();
        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.Id == third);
        Assert.Equal(world.HighPriorityEventId, forced.CanonicalEventId);  // priority still picks canonical.

        // The claimed copy left its signature pair ⇒ that auto group dissolved.
        Assert.Single(groups);
    }

    [Fact]
    public async Task The_inspector_reports_members_with_the_canonical_marked()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var world = await SeedTwoCopiesAsync(db);
        var service = NewService(db);
        await new DedupGrouper(db).RegroupAsync(CancellationToken.None);

        var group = await service.GetGroupForEventAsync(world.LowPriorityEventId, CancellationToken.None);

        Assert.NotNull(group);
        Assert.Equal(2, group!.Members.Count);
        Assert.True(group.Members.Single(m => m.EventId == world.HighPriorityEventId).IsCanonical);
        Assert.False(group.Members.Single(m => m.EventId == world.LowPriorityEventId).IsCanonical);
        Assert.Equal("Personal", group.Members.Single(m => m.EventId == world.LowPriorityEventId).AccountName);
    }

    [Fact]
    public async Task Validation_rejects_malformed_overrides()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateOverrideAsync(
            OverrideKind.SetCanonical, new[] { "a", "b" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateOverrideAsync(
            OverrideKind.ForceMerge, new[] { "only-one" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateOverrideAsync(
            OverrideKind.NeverMerge, Array.Empty<string>(), null, CancellationToken.None));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private sealed record World(
        Guid HighPriorityEventId, Guid LowPriorityEventId, Guid HighPriorityCalendarId);

    private static DuplicateService NewService(CalendarDbContext db) =>
        new(db, new DedupGrouper(db), new DeviceProvider(db));

    /// <summary>Two accounts (Work priority 0, Personal priority 1), one calendar each, one shared-signature copy each.</summary>
    private static async Task<World> SeedTwoCopiesAsync(CalendarDbContext db)
    {
        db.Plugins.Add(new Domain.Entities.Plugin
        {
            Id = "org.unifiedcalendar.ics", Name = "ICS", Version = "1.0", SdkVersion = "1.x",
            Kind = PluginKind.Assembly, Manifest = "{}",
        });
        var work = NewAccount("Work", priority: 0);
        var personal = NewAccount("Personal", priority: 1);
        var workCal = NewCalendar(work.Id);
        var personalCal = NewCalendar(personal.Id);
        db.Accounts.AddRange(work, personal);
        db.Calendars.AddRange(workCal, personalCal);
        await db.SaveChangesAsync();

        var high = await SeedEventAsync(db, workCal.Id, "New Year Party", "uid-high", "sig-shared");
        var low = await SeedEventAsync(db, personalCal.Id, "New Year Party", "uid-low", "sig-shared");
        return new World(high, low, workCal.Id);
    }

    private static Account NewAccount(string name, int priority) => new()
    {
        Id = Guid.CreateVersion7(), PluginId = "org.unifiedcalendar.ics", DisplayName = name,
        Priority = priority, UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
    };

    private static CalendarEntity NewCalendar(Guid accountId) => new()
    {
        Id = Guid.CreateVersion7(), AccountId = accountId, RemoteId = Guid.NewGuid().ToString("N"),
        Name = "Cal", IsVisible = true, UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
    };

    private static async Task<Guid> SeedEventAsync(
        CalendarDbContext db, Guid calendarId, string title, string uid, string signature)
    {
        var ev = new Event
        {
            Id = Guid.CreateVersion7(), CalendarId = calendarId, RemoteId = uid, Uid = uid,
            Title = title, DedupSignature = signature,
            StartUtc = new DateTimeOffset(2026, 12, 31, 20, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
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
