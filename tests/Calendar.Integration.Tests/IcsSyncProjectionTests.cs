using System.Net;
using System.Text;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.Ics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 1 end-to-end: connect an ICS feed → sync normalizes events into the store → the projection service
/// expands recurrence, applies visibility, and collapses duplicates (the "first demo" path).
/// </summary>
public sealed class IcsSyncProjectionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-ics-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Connecting_a_feed_renders_its_events_in_the_window()
    {
        const string feed = "https://feeds.test/holidays.ics";
        var handler = MapHandler((feed, """
            BEGIN:VCALENDAR
            X-WR-CALNAME:Holidays
            BEGIN:VEVENT
            UID:newyear@test
            SUMMARY:New Year's Day
            DTSTART;VALUE=DATE:20260101
            DTEND;VALUE=DATE:20260102
            END:VEVENT
            END:VCALENDAR
            """));

        using var db = NewDb();
        db.Database.Migrate();
        var (accounts, projection, _) = BuildServices(db, handler);

        await accounts.ConnectIcsAsync(feed, "Holidays", 60, null, CancellationToken.None);

        var events = await projection.GetEventsAsync(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var ev = Assert.Single(events);
        Assert.Equal("New Year's Day", ev.Title);
        Assert.True(ev.AllDay);
        Assert.Equal("Holidays", ev.CalendarName);
    }

    [Fact]
    public async Task Recurring_event_expands_to_one_instance_per_occurrence()
    {
        const string feed = "https://feeds.test/weekly.ics";
        var handler = MapHandler((feed, """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly@test
            SUMMARY:Weekly Sync
            DTSTART:20260106T100000Z
            DTEND:20260106T103000Z
            RRULE:FREQ=WEEKLY;BYDAY=TU
            END:VEVENT
            END:VCALENDAR
            """));

        using var db = NewDb();
        db.Database.Migrate();
        var (accounts, projection, _) = BuildServices(db, handler);

        await accounts.ConnectIcsAsync(feed, "Work", 60, null, CancellationToken.None);

        // Jan 6, 13, 20 2026 are Tuesdays → three occurrences in this window.
        var events = await projection.GetEventsAsync(
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 23, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.True(e.IsRecurringInstance));
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 10, 0, 0, TimeSpan.Zero), events[0].StartUtc);
    }

    [Fact]
    public async Task Hiding_a_calendar_removes_its_events_from_the_projection()
    {
        const string feed = "https://feeds.test/h.ics";
        var handler = MapHandler((feed, SingleDay("x@test", "Thing", "20260115")));

        using var db = NewDb();
        db.Database.Migrate();
        var (accounts, projection, catalog) = BuildServices(db, handler);

        await accounts.ConnectIcsAsync(feed, "Feed", 60, null, CancellationToken.None);
        var calendar = Assert.Single(await catalog.ListCalendarsAsync(CancellationToken.None));
        await catalog.SetCalendarVisibilityAsync(calendar.Id, isVisible: false, CancellationToken.None);

        var events = await projection.GetEventsAsync(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Empty(events);
    }

    [Fact]
    public async Task The_same_holiday_in_two_feeds_collapses_to_one_canonical()
    {
        const string feedA = "https://feeds.test/a.ics";
        const string feedB = "https://feeds.test/b.ics";
        var handler = MapHandler(
            (feedA, SingleDay("ny@test", "New Year", "20260101")),
            (feedB, SingleDay("ny@test", "New Year", "20260101")));

        using var db = NewDb();
        db.Database.Migrate();
        var (accounts, projection, _) = BuildServices(db, handler);

        await accounts.ConnectIcsAsync(feedA, "Personal", 60, null, CancellationToken.None);
        await accounts.ConnectIcsAsync(feedB, "Work", 60, null, CancellationToken.None);

        var events = await projection.GetEventsAsync(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var ev = Assert.Single(events);                 // duplicates collapsed
        Assert.Equal(1, ev.DuplicateCount);             // "+1 duplicate" affordance
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static string SingleDay(string uid, string summary, string date) => $"""
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:{uid}
        SUMMARY:{summary}
        DTSTART;VALUE=DATE:{date}
        END:VEVENT
        END:VCALENDAR
        """;

    private static (IAccountService Accounts, IEventProjectionService Projection, ICalendarCatalog Catalog)
        BuildServices(CalendarDbContext db, HttpMessageHandler handler)
    {
        var registry = new PluginRegistry();
        var icsPlugin = new IcsPlugin();
        registry.Register(new PluginRegistration(
            icsPlugin.Manifest,
            new[] { Capability.CalendarRead },
            PluginState.Running,
            new TestInstance(icsPlugin.Manifest.Id, icsPlugin)));

        var device = new DeviceProvider(db);
        var vault = new SecretVault(db);
        var dedup = new DedupGrouper(db);
        var cache = new InMemoryPluginCache();
        var http = new HttpClient(handler);

        var geocodeAggregator = new Calendar.Infrastructure.Aggregation.GeocodeAggregator(
            registry,
            new Calendar.Infrastructure.Aggregation.InMemoryAggregationResultCache(),
            NullLogger<Calendar.Infrastructure.Aggregation.GeocodeAggregator>.Instance);
        var geocode = new GeocodeService(
            db, geocodeAggregator, registry, device, NullLogger<GeocodeService>.Instance);

        var sync = new CalendarSyncService(db, registry, vault, cache, http, dedup, device, geocode, NullLoggerFactory.Instance);
        var accounts = new AccountService(db, vault, sync, registry, device);
        var projection = new EventProjectionService(db);
        var catalog = new CalendarCatalog(db, device);
        return (accounts, projection, catalog);
    }

    private static HttpMessageHandler MapHandler(params (string Url, string Body)[] feeds)
    {
        var map = feeds.ToDictionary(f => f.Url, f => f.Body, StringComparer.OrdinalIgnoreCase);
        return new MapHttpHandler(map);
    }

    private sealed class MapHttpHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, string> _map;
        public MapHttpHandler(IReadOnlyDictionary<string, string> map) => _map = map;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_map.TryGetValue(url, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/calendar"),
            };
            response.Headers.TryAddWithoutValidation("ETag", $"\"{body.GetHashCode():x}\"");
            return Task.FromResult(response);
        }
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
