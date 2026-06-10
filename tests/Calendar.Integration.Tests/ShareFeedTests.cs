using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Ical.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;
using PluginEntity = Calendar.Domain.Entities.Plugin;
using EventStatus = Calendar.Plugin.Abstractions.EventStatus;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 6 sharing (ARCHITECTURE §16): tokenized read-only ICS shares. Verifies the public feed serves valid
/// iCalendar, FreeBusy hides titles/locations, expiry → 410 / revoked|unknown → 404 (via the resolve result),
/// union filters select the right events, and revocation stops the token resolving. The served ICS is parsed
/// back with Ical.Net to assert event count/fields end-to-end.
/// </summary>
public sealed class ShareFeedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-share-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private static (ShareService Shares, EventProjectionService Projection) Build(CalendarDbContext db)
    {
        var device = new DeviceProvider(db);
        var shares = new ShareService(db, device, NullLogger<ShareService>.Instance);
        var projection = new EventProjectionService(db);
        return (shares, projection);
    }

    [Fact]
    public async Task FullDetails_feed_serves_valid_icalendar_with_titles_and_locations()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        SeedEvent(db, calA, "ev1", "Dentist", "Main St Clinic", new DateTimeOffset(2030, 1, 10, 9, 0, 0, TimeSpan.Zero));
        db.SaveChanges();

        var (shares, projection) = Build(db);
        var dto = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FullDetails, null), default);

        var ics = await ServeAsync(shares, projection, dto.Token);
        Assert.NotNull(ics);

        var parsed = Ical.Net.Calendar.Load(ics!);
        var ev = Assert.Single(parsed.Events);
        Assert.Equal("Dentist", ev.Summary);
        Assert.Equal("Main St Clinic", ev.Location);
    }

    [Fact]
    public async Task FreeBusy_feed_hides_titles_and_locations()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        SeedEvent(db, calA, "ev1", "Secret therapy", "123 Private Rd", new DateTimeOffset(2030, 2, 5, 14, 0, 0, TimeSpan.Zero));
        db.SaveChanges();

        var (shares, projection) = Build(db);
        var dto = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FreeBusy, null), default);

        var ics = await ServeAsync(shares, projection, dto.Token);
        var parsed = Ical.Net.Calendar.Load(ics!);
        var ev = Assert.Single(parsed.Events);

        Assert.DoesNotContain("Secret therapy", ics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private Rd", ics, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ev.Location);
        Assert.Equal("Busy", ev.Summary);
        // The busy block still carries the real times.
        Assert.Equal(new DateTime(2030, 2, 5, 14, 0, 0, DateTimeKind.Utc), ev.Start.AsUtc);
    }

    [Fact]
    public async Task Expired_share_resolves_as_gone()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        db.SaveChanges();

        var (shares, _) = Build(db);
        var dto = await shares.CreateAsync(
            new CreateShareRequest(calA, null, ShareScope.FullDetails, DateTimeOffset.UtcNow.AddMinutes(-1)), default);

        var resolved = await shares.ResolveAsync(dto.Token, default);
        Assert.Equal(ShareResolution.Expired, resolved.Resolution); // → 410 Gone
    }

    [Fact]
    public async Task Unknown_token_resolves_as_not_found()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (shares, _) = Build(db);

        var resolved = await shares.ResolveAsync("totally-unknown-token", default);
        Assert.Equal(ShareResolution.NotFound, resolved.Resolution); // → 404
    }

    [Fact]
    public async Task Revoking_a_share_makes_the_feed_stop_resolving()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        db.SaveChanges();

        var (shares, _) = Build(db);
        var dto = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FullDetails, null), default);

        Assert.Equal(ShareResolution.Ok, (await shares.ResolveAsync(dto.Token, default)).Resolution);

        var revoked = await shares.RevokeAsync(dto.Id, default);
        Assert.True(revoked);

        // Revoked tombstone → indistinguishable from unknown → 404.
        Assert.Equal(ShareResolution.NotFound, (await shares.ResolveAsync(dto.Token, default)).Resolution);
        // And it disappears from the owner's list.
        Assert.Empty(await shares.ListAsync(default));
    }

    [Fact]
    public async Task Union_filter_selects_only_the_chosen_calendars()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        var (calB, _) = Seed(db, "Work");
        var (calC, _) = Seed(db, "Hidden");
        SeedEvent(db, calA, "a1", "Personal lunch", null, new DateTimeOffset(2030, 3, 1, 12, 0, 0, TimeSpan.Zero));
        SeedEvent(db, calB, "b1", "Work review", null, new DateTimeOffset(2030, 3, 2, 12, 0, 0, TimeSpan.Zero));
        SeedEvent(db, calC, "c1", "Excluded", null, new DateTimeOffset(2030, 3, 3, 12, 0, 0, TimeSpan.Zero));
        db.SaveChanges();

        var (shares, projection) = Build(db);
        var filter = new ShareFilter(new[] { calA, calB }, Array.Empty<Guid>());
        var dto = await shares.CreateAsync(new CreateShareRequest(null, filter, ShareScope.FullDetails, null), default);

        var ics = await ServeAsync(shares, projection, dto.Token);
        var parsed = Ical.Net.Calendar.Load(ics!);

        var summaries = parsed.Events.Select(e => e.Summary).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Personal lunch", "Work review" }, summaries);
        Assert.DoesNotContain("Excluded", ics, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Union_filter_with_categories_narrows_to_tagged_events()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        var travel = SeedCategory(db, "Travel");
        var e1 = SeedEvent(db, calA, "a1", "Flight to Berlin", null, new DateTimeOffset(2030, 4, 1, 8, 0, 0, TimeSpan.Zero));
        SeedEvent(db, calA, "a2", "Grocery run", null, new DateTimeOffset(2030, 4, 2, 8, 0, 0, TimeSpan.Zero));
        db.EventCategories.Add(new EventCategory { EventId = e1, CategoryId = travel, AssignedBy = AssignmentSource.Rule });
        db.SaveChanges();

        var (shares, projection) = Build(db);
        var filter = new ShareFilter(new[] { calA }, new[] { travel });
        var dto = await shares.CreateAsync(new CreateShareRequest(null, filter, ShareScope.FullDetails, null), default);

        var ics = await ServeAsync(shares, projection, dto.Token);
        var parsed = Ical.Net.Calendar.Load(ics!);

        var ev = Assert.Single(parsed.Events);
        Assert.Equal("Flight to Berlin", ev.Summary);
    }

    [Fact]
    public async Task Hidden_calendar_can_still_be_published()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, calEntity) = Seed(db, "Hidden but shared");
        calEntity.IsVisible = false; // hidden in the owner's UI
        SeedEvent(db, calA, "x1", "Still shared", null, new DateTimeOffset(2030, 5, 1, 10, 0, 0, TimeSpan.Zero));
        db.SaveChanges();

        var (shares, projection) = Build(db);
        var dto = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FullDetails, null), default);

        var ics = await ServeAsync(shares, projection, dto.Token);
        var parsed = Ical.Net.Calendar.Load(ics!);
        Assert.Single(parsed.Events);
    }

    [Fact]
    public async Task Tokens_are_high_entropy_and_unique()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (calA, _) = Seed(db, "Personal");
        db.SaveChanges();

        var (shares, _) = Build(db);
        var a = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FullDetails, null), default);
        var b = await shares.CreateAsync(new CreateShareRequest(calA, null, ShareScope.FullDetails, null), default);

        Assert.NotEqual(a.Token, b.Token);
        Assert.True(a.Token.Length >= 40, "token should be high-entropy (~43 base64url chars for 256 bits)");
        Assert.DoesNotContain('+', a.Token);
        Assert.DoesNotContain('/', a.Token);
        Assert.DoesNotContain('=', a.Token);
    }

    // ── the feed endpoint's projection→serialize path, exercised without an HTTP host ──
    private static async Task<string?> ServeAsync(IShareService shares, IEventProjectionService projection, string token)
    {
        var resolved = await shares.ResolveAsync(token, default);
        if (resolved.Resolution != ShareResolution.Ok)
            return null;
        var share = resolved.Share!;
        var now = DateTimeOffset.UtcNow;
        var events = await projection.GetEventsForShareAsync(
            share.CalendarIds, share.CategoryIds, now.AddMonths(-1), now.AddYears(6), default);
        return ShareFeedSerializer.Serialize(share.FeedName, share.Scope, events);
    }

    // ── seeding helpers (direct entity inserts; no plugin host needed) ──
    private static (Guid CalendarId, CalendarEntity Entity) Seed(CalendarDbContext db, string calendarName)
    {
        var pluginId = "test.plugin";
        var alreadyTracked = db.Plugins.Local.Any(p => p.Id == pluginId);
        if (!alreadyTracked && !db.Plugins.Any(p => p.Id == pluginId))
        {
            db.Plugins.Add(new PluginEntity
            {
                Id = pluginId, Name = "Test", Version = "1.0.0", SdkVersion = "1.0.0",
                Kind = Calendar.Plugin.Abstractions.PluginKind.Assembly, Status = PluginStatus.Installed,
                Manifest = "{}", TrustTier = TrustTier.InBox, InstalledAtUtc = DateTimeOffset.UtcNow,
            });
        }

        var accountId = Guid.CreateVersion7();
        db.Accounts.Add(new Account
        {
            Id = accountId, PluginId = pluginId, DisplayName = calendarName,
            Status = AccountStatus.Connected, UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = Guid.Empty, Lamport = 1,
        });

        var cal = new CalendarEntity
        {
            Id = Guid.CreateVersion7(), AccountId = accountId, RemoteId = Guid.NewGuid().ToString(),
            Name = calendarName, IsVisible = true, IsReadOnly = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Calendars.Add(cal);
        return (cal.Id, cal);
    }

    private static Guid SeedEvent(CalendarDbContext db, Guid calendarId, string uid, string title, string? location, DateTimeOffset start)
    {
        var id = Guid.CreateVersion7();
        db.Events.Add(new Event
        {
            Id = id, CalendarId = calendarId, Uid = uid, RemoteId = uid,
            Title = title, Location = location,
            StartUtc = start, EndUtc = start.AddHours(1), AllDay = false,
            Status = EventStatus.Confirmed, RowVersion = Guid.NewGuid().ToString("N"),
        });
        return id;
    }

    private static Guid SeedCategory(CalendarDbContext db, string name)
    {
        var id = Guid.CreateVersion7();
        db.Categories.Add(new Category
        {
            Id = id, Name = name, IsVisible = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        });
        return id;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
