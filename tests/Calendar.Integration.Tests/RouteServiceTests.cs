using Calendar.Application.Aggregation;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Aggregation;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;
using PlaceEntity = Calendar.Domain.Entities.Place;

namespace Calendar.Integration.Tests;

/// <summary>
/// The host routing pipeline (ARCHITECTURE §7, geo-routing-plugin.md §4–§5): a provider's <c>DurationSec</c>
/// → a <see cref="RouteLeg"/> with HOST-COMPUTED <c>LeaveByUtc</c> and <c>Feasible</c>, cached by
/// <c>(From,To,Mode)</c> and reused without a second call, with mode-coverage failover and a stale fallback
/// when every provider fails. No live network — a fake <see cref="IRouteProvider"/> stands in and counts calls.
/// The provider-only contract (no leave-by / feasibility) is asserted to stay host-side.
/// </summary>
public sealed class RouteServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-route-{Guid.NewGuid():N}.db");

    // A fixed 5-minute buffer keeps the leave-by/feasibility arithmetic exact and obvious in the assertions.
    private static readonly TimeSpan Buffer = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Day = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Duration_is_mapped_and_leave_by_plus_feasibility_are_host_computed()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var fromPlace = await SeedPlaceAsync(db, "Office", 52.5, 13.4);
        var toPlace = await SeedPlaceAsync(db, "Client", 52.6, 13.5);
        // prev 09:00–10:00, next 11:00–12:00 → gap 60 min; drive takes 20 min.
        await SeedEventAsync(db, cal, "Standup", Day.AddHours(9), Day.AddHours(10), fromPlace);
        var next = await SeedEventAsync(db, cal, "Pitch", Day.AddHours(11), Day.AddHours(12), toPlace);

        var provider = new FakeRouteProvider(durationSec: 1200, FullCoverage); // 20 min
        var service = BuildService(db, provider);

        var leg = await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.NotNull(leg);
        Assert.Equal(1200, leg!.DurationSec);                       // straight from the provider.
        // leaveBy = nextStart(11:00) − 20min − 5min buffer = 10:35.
        Assert.Equal(Day.AddHours(10).AddMinutes(35), leg.LeaveByUtc);
        Assert.True(leg.Feasible);                                  // gap 60min ≥ 20min.
        Assert.False(leg.IsStale);
        Assert.Equal("org.unifiedcalendar.fake.route", leg.Source); // aggregator stamps the WINNING PLUGIN ID.

        // The provider never invented the host-computed fields — RouteLeg carries them, the provider's
        // RouteResult left them at defaults (null / true).
        var stored = await db.RouteLegs.AsNoTracking().SingleAsync();
        Assert.Equal(next, stored.ToEventId);
        Assert.NotNull(stored.LeaveByUtc);
    }

    [Fact]
    public async Task An_impossible_gap_is_infeasible()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.9, 13.9);
        // prev ends 10:00, next starts 10:10 → 10-minute gap, but the drive is 40 minutes. Can't make it.
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(10).AddMinutes(10), Day.AddHours(11), b);

        var leg = await BuildService(db, new FakeRouteProvider(2400, FullCoverage))
            .GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.NotNull(leg);
        Assert.False(leg!.Feasible);                                // gap 10min < 40min ⇒ "you can't make it".
    }

    [Theory]
    [InlineData(1200, true)]    // gap == duration exactly → feasible (boundary).
    [InlineData(1201, false)]   // one second over → infeasible.
    public async Task Feasibility_flips_at_the_gap_boundary(int durationSec, bool expectedFeasible)
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        // gap is exactly 20 minutes (1200s).
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(10).AddMinutes(20), Day.AddHours(11), b);

        var leg = await BuildService(db, new FakeRouteProvider(durationSec, FullCoverage))
            .GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.Equal(expectedFeasible, leg!.Feasible);
    }

    [Fact]
    public async Task A_cached_leg_is_reused_without_a_second_provider_call()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), b);

        var provider = new FakeRouteProvider(900, FullCoverage);
        var service = BuildService(db, provider);

        await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);
        await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.Equal(1, provider.Calls);                            // endpoints unchanged ⇒ one outbound call.
        Assert.Single(await db.RouteLegs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Moving_an_endpoint_place_invalidates_and_re_routes_the_leg()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), b);

        var provider = new FakeRouteProvider(900, FullCoverage);
        var service = BuildService(db, provider);
        await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        // The destination Place moves far away → the input fingerprint changes → must re-route.
        var place = await db.Places.FirstAsync(p => p.Label == "B");
        place.Lat = 53.9;
        place.Lng = 14.9;
        await db.SaveChangesAsync();

        await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.Equal(2, provider.Calls);                            // endpoint moved ⇒ a second call.
        Assert.Single(await db.RouteLegs.AsNoTracking().ToListAsync()); // still one upserted row for the pair.
    }

    [Fact]
    public async Task A_time_only_change_recomputes_feasibility_without_re_routing()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), b);

        var provider = new FakeRouteProvider(1200, FullCoverage);  // 20 min drive.
        var service = BuildService(db, provider);
        var first = await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);
        Assert.True(first!.Feasible);                              // gap 60min ≥ 20min.

        // The next event is pulled earlier so only a 10-minute gap remains — now infeasible, no re-route.
        var ev = await db.Events.FirstAsync(e => e.Id == next);
        ev.StartUtc = Day.AddHours(10).AddMinutes(10);
        await db.SaveChangesAsync();

        var second = await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.False(second!.Feasible);                           // recomputed from the new gap.
        Assert.Equal(1, provider.Calls);                          // DurationSec unchanged ⇒ no new outbound call.
    }

    [Fact]
    public async Task A_transit_query_skips_a_drive_only_provider_and_fails_over_to_a_transit_one()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), b);

        // OSRM-like: drive/walk/bike but NO transit. A transit-capable provider answers 30 min.
        var osrmLike = new FakeRouteProvider(600, new RouteCoverage(true, false, true, true, false),
            id: "org.unifiedcalendar.geo.osrm", source: "osrm");
        var transit = new FakeRouteProvider(1800, new RouteCoverage(false, true, false, false, false),
            id: "org.unifiedcalendar.geo.transit", source: "transit");

        var service = BuildService(db, osrmLike, transit);

        var leg = await service.GetCommuteAsync(next, TravelMode.Transit, CancellationToken.None);

        Assert.NotNull(leg);
        Assert.Equal(1800, leg!.DurationSec);                     // the transit provider's answer.
        Assert.Equal("org.unifiedcalendar.geo.transit", leg.Source); // winning plugin id.
        Assert.Equal(0, osrmLike.Calls);                          // coverage-skipped before any call.
        Assert.Equal(1, transit.Calls);
    }

    [Fact]
    public async Task When_every_provider_fails_the_last_known_leg_is_served_stale()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), a);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), b);

        // First call succeeds and caches a leg.
        var provider = new FakeRouteProvider(900, FullCoverage);
        var registry = BuildRegistry(provider);
        var aggregator = new RouteAggregator(registry, new InMemoryAggregationResultCache(),
            NullLogger<RouteAggregator>.Instance);
        var service = new RouteService(db, aggregator, registry, Buffer, NullLogger<RouteService>.Instance);
        var fresh = await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);
        Assert.False(fresh!.IsStale);

        // Now the provider starts throwing. Force a re-route by moving the endpoint so the cache is invalidated.
        provider.Fail = true;
        var place = await db.Places.FirstAsync(p => p.Label == "B");
        place.Lat = 54.9;
        place.Lng = 15.9;
        await db.SaveChangesAsync();

        var stale = await service.GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.NotNull(stale);
        Assert.True(stale!.IsStale);                              // chip greys, doesn't vanish.
        Assert.Equal(900, stale.DurationSec);                     // last-known duration retained.
    }

    [Fact]
    public async Task No_preceding_placed_event_yields_no_leg()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var b = await SeedPlaceAsync(db, "B", 52.6, 13.5);
        // The only placed event of the day → nothing to commute from.
        var only = await SeedEventAsync(db, cal, "Solo", Day.AddHours(11), Day.AddHours(12), b);

        var leg = await BuildService(db, new FakeRouteProvider(900, FullCoverage))
            .GetCommuteAsync(only, TravelMode.Drive, CancellationToken.None);

        Assert.Null(leg);
    }

    [Fact]
    public async Task Same_place_back_to_back_events_yield_no_leg()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var cal = await SeedCalendarAsync(db);
        var here = await SeedPlaceAsync(db, "Here", 52.5, 13.4);
        await SeedEventAsync(db, cal, "Prev", Day.AddHours(9), Day.AddHours(10), here);
        var next = await SeedEventAsync(db, cal, "Next", Day.AddHours(11), Day.AddHours(12), here);

        var provider = new FakeRouteProvider(900, FullCoverage);
        var leg = await BuildService(db, provider).GetCommuteAsync(next, TravelMode.Drive, CancellationToken.None);

        Assert.Null(leg);                                         // same Place ⇒ no commute.
        Assert.Equal(0, provider.Calls);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static readonly RouteCoverage FullCoverage = new(true, true, true, true, false);

    private static RouteService BuildService(CalendarDbContext db, params FakeRouteProvider[] providers)
    {
        var registry = BuildRegistry(providers);
        var aggregator = new RouteAggregator(registry, new InMemoryAggregationResultCache(),
            NullLogger<RouteAggregator>.Instance);
        return new RouteService(db, aggregator, registry, Buffer, NullLogger<RouteService>.Instance);
    }

    private static PluginRegistry BuildRegistry(params FakeRouteProvider[] providers)
    {
        var registry = new PluginRegistry();
        foreach (var p in providers)
            registry.Register(new PluginRegistration(
                p.Manifest, new[] { Capability.GeoRoute }, PluginState.Running,
                new TestInstance(p.Manifest.Id, p)));
        return registry;
    }

    private static async Task<Guid> SeedCalendarAsync(CalendarDbContext db)
    {
        var account = new Account
        {
            Id = Guid.CreateVersion7(), PluginId = "org.unifiedcalendar.ics", DisplayName = "Test",
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        var plugin = new Domain.Entities.Plugin
        {
            Id = "org.unifiedcalendar.ics", Name = "ICS", Version = "1.0", SdkVersion = "1.x",
            Kind = PluginKind.Assembly, Manifest = "{}",
        };
        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(), AccountId = account.Id, RemoteId = "cal-1", Name = "Cal",
            IsVisible = true, UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Plugins.Add(plugin);
        db.Accounts.Add(account);
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar.Id;
    }

    private static async Task<Guid> SeedPlaceAsync(CalendarDbContext db, string label, double lat, double lng)
    {
        var place = new PlaceEntity
        {
            Id = Guid.CreateVersion7(), Label = label, Lat = lat, Lng = lng,
            NormalizedKey = $"{label}:{Guid.NewGuid():N}", Source = "fake",
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return place.Id;
    }

    private static async Task<Guid> SeedEventAsync(
        CalendarDbContext db, Guid calendarId, string title,
        DateTimeOffset start, DateTimeOffset end, Guid placeId)
    {
        var ev = new Event
        {
            Id = Guid.CreateVersion7(), CalendarId = calendarId,
            RemoteId = Guid.NewGuid().ToString("N"), Uid = Guid.NewGuid().ToString("N"),
            Title = title, StartUtc = start, EndUtc = end, PlaceId = placeId,
            Status = EventStatus.Confirmed, RowVersion = Guid.NewGuid().ToString("N"),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    /// <summary>
    /// A fake <see cref="IRouteProvider"/>: returns a fixed duration, advertises a given <see cref="RouteCoverage"/>,
    /// counts outbound calls, and can be flipped to throw. It honors the SDK contract — LeaveByUtc null,
    /// Feasible true — so the host stays the sole author of those fields.
    /// </summary>
    private sealed class FakeRouteProvider : IRouteProvider
    {
        private readonly int _durationSec;
        private readonly string _source;
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public FakeRouteProvider(
            int durationSec, RouteCoverage coverage,
            string id = "org.unifiedcalendar.fake.route", string source = "fake-route")
        {
            _durationSec = durationSec;
            _source = source;
            Coverage = coverage;
            Manifest = new PluginManifest(
                id, "Fake Router", "1.0", "1.x", PluginKind.Declarative,
                new[] { CapabilityIds.GeoRoute }, Publisher: null,
                new AuthSpec(AuthScheme.None), new NetworkSpec(new[] { "*" }),
                new ConfigSchema("{}", Array.Empty<string>()));
        }

        public RouteCoverage Coverage { get; }
        public PluginManifest Manifest { get; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<RouteResult> RouteAsync(
            GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct)
        {
            Calls++;
            if (Fail)
                throw new HttpRequestException("provider down");
            // Provider-only contract: DurationSec (+ Geometry); LeaveByUtc null, Feasible true.
            return Task.FromResult(new RouteResult(_durationSec, "geom", LeaveByUtc: null, Feasible: true, _source));
        }
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
