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
/// Trips drawn as routes (ROADMAP Phase 5): placed Travel-category events become ordered trip legs, road
/// geometry rides through the geo.route aggregator + RouteLeg cache, gaps split trips, and a missing
/// provider degrades to straight-line legs — never a crash, never a missing trip.
/// </summary>
public sealed class TripRouteServiceTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-trips-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Travel_events_become_one_ordered_trip_and_other_events_stay_off_the_route()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var seed = await SeedWorldAsync(db);

        var home = await SeedPlaceAsync(db, "Berlin", 52.5, 13.4);
        var stop1 = await SeedPlaceAsync(db, "Paris", 48.85, 2.35);
        var stop2 = await SeedPlaceAsync(db, "Lyon", 45.76, 4.84);
        var office = await SeedPlaceAsync(db, "Office", 52.51, 13.41);

        await SeedEventAsync(db, seed, "Flight to Paris", Day, Day.AddHours(2), home, travel: true);
        await SeedEventAsync(db, seed, "Standup", Day.AddHours(3), Day.AddHours(4), office, travel: false); // not a stop
        await SeedEventAsync(db, seed, "Hotel Paris", Day.AddDays(1), Day.AddDays(1).AddHours(1), stop1, travel: true);
        await SeedEventAsync(db, seed, "TGV to Lyon", Day.AddDays(3), Day.AddDays(3).AddHours(2), stop2, travel: true);

        var provider = new FakeRouteProvider("encoded-geom");
        var trips = await BuildService(db, provider).GetTripRoutesAsync(
            Day.AddDays(-1), Day.AddDays(30), CancellationToken.None);

        var trip = Assert.Single(trips);
        Assert.Equal("Berlin → Lyon", trip.Name);
        Assert.Equal(2, trip.Legs.Count);
        Assert.Equal("Berlin", trip.Legs[0].FromLabel);
        Assert.Equal("Paris", trip.Legs[0].ToLabel);
        Assert.Equal("Paris", trip.Legs[1].FromLabel);
        Assert.Equal("Lyon", trip.Legs[1].ToLabel);
        Assert.All(trip.Legs, l => Assert.Equal("encoded-geom", l.Geometry));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Without_a_route_provider_legs_degrade_to_straight_lines()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var seed = await SeedWorldAsync(db);

        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 48.85, 2.35);
        await SeedEventAsync(db, seed, "Go", Day, Day.AddHours(2), a, travel: true);
        await SeedEventAsync(db, seed, "Arrive", Day.AddDays(1), Day.AddDays(1).AddHours(1), b, travel: true);

        var trips = await BuildService(db /* no providers */).GetTripRoutesAsync(
            Day.AddDays(-1), Day.AddDays(30), CancellationToken.None);

        var leg = Assert.Single(Assert.Single(trips).Legs);
        Assert.Null(leg.Geometry);                          // client draws the dashed straight line.
        Assert.Equal(52.5, leg.FromLat);
        Assert.Equal(2.35, leg.ToLng);
    }

    [Fact]
    public async Task A_gap_longer_than_a_week_starts_a_new_trip()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var seed = await SeedWorldAsync(db);

        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 48.85, 2.35);
        var c = await SeedPlaceAsync(db, "C", 41.39, 2.17);
        var d = await SeedPlaceAsync(db, "D", 45.46, 9.19);

        // Trip 1: A → B in early July. Trip 2: C → D three weeks later.
        await SeedEventAsync(db, seed, "Out", Day, Day.AddHours(2), a, travel: true);
        await SeedEventAsync(db, seed, "There", Day.AddDays(1), Day.AddDays(1).AddHours(1), b, travel: true);
        await SeedEventAsync(db, seed, "Out2", Day.AddDays(22), Day.AddDays(22).AddHours(2), c, travel: true);
        await SeedEventAsync(db, seed, "There2", Day.AddDays(23), Day.AddDays(23).AddHours(1), d, travel: true);

        var trips = await BuildService(db).GetTripRoutesAsync(
            Day.AddDays(-1), Day.AddDays(40), CancellationToken.None);

        Assert.Equal(2, trips.Count);
        Assert.Equal("A → B", trips[0].Name);
        Assert.Equal("C → D", trips[1].Name);
    }

    [Fact]
    public async Task Leg_geometry_is_cached_so_a_second_map_open_makes_no_outbound_calls()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var seed = await SeedWorldAsync(db);

        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 48.85, 2.35);
        await SeedEventAsync(db, seed, "Go", Day, Day.AddHours(2), a, travel: true);
        await SeedEventAsync(db, seed, "Arrive", Day.AddDays(1), Day.AddDays(1).AddHours(1), b, travel: true);

        var provider = new FakeRouteProvider("geom");
        var service = BuildService(db, provider);

        await service.GetTripRoutesAsync(Day.AddDays(-1), Day.AddDays(30), CancellationToken.None);
        await service.GetTripRoutesAsync(Day.AddDays(-1), Day.AddDays(30), CancellationToken.None);

        Assert.Equal(1, provider.Calls);                    // second pass served from the RouteLeg cache.
    }

    [Fact]
    public async Task Consecutive_stops_at_the_same_place_draw_no_leg()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var seed = await SeedWorldAsync(db);

        var a = await SeedPlaceAsync(db, "A", 52.5, 13.4);
        var b = await SeedPlaceAsync(db, "B", 48.85, 2.35);
        await SeedEventAsync(db, seed, "Go", Day, Day.AddHours(2), a, travel: true);
        await SeedEventAsync(db, seed, "Hotel night 1", Day.AddDays(1), Day.AddDays(1).AddHours(1), b, travel: true);
        await SeedEventAsync(db, seed, "Hotel night 2", Day.AddDays(2), Day.AddDays(2).AddHours(1), b, travel: true);

        var trips = await BuildService(db).GetTripRoutesAsync(
            Day.AddDays(-1), Day.AddDays(30), CancellationToken.None);

        Assert.Single(Assert.Single(trips).Legs);           // A→B once; B→B skipped.
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private sealed record SeedContext(Guid CalendarId, Guid TravelCategoryId);

    private static TripRouteService BuildService(CalendarDbContext db, params FakeRouteProvider[] providers)
    {
        var registry = new PluginRegistry();
        foreach (var p in providers)
            registry.Register(new PluginRegistration(
                p.Manifest, new[] { Capability.GeoRoute }, PluginState.Running,
                new TestInstance(p.Manifest.Id, p)));
        var aggregator = new RouteAggregator(registry, new InMemoryAggregationResultCache(),
            NullLogger<RouteAggregator>.Instance);
        return new TripRouteService(db, aggregator, registry, NullLogger<TripRouteService>.Instance);
    }

    private static async Task<SeedContext> SeedWorldAsync(CalendarDbContext db)
    {
        var plugin = new Domain.Entities.Plugin
        {
            Id = "org.unifiedcalendar.ics", Name = "ICS", Version = "1.0", SdkVersion = "1.x",
            Kind = PluginKind.Assembly, Manifest = "{}",
        };
        var account = new Account
        {
            Id = Guid.CreateVersion7(), PluginId = plugin.Id, DisplayName = "Test",
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(), AccountId = account.Id, RemoteId = "cal-1", Name = "Cal",
            IsVisible = true, UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        var travel = new Category
        {
            Id = Guid.CreateVersion7(), Name = "Travel", IsVisible = true, IsBuiltIn = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Plugins.Add(plugin);
        db.Accounts.Add(account);
        db.Calendars.Add(calendar);
        db.Categories.Add(travel);
        await db.SaveChangesAsync();
        return new SeedContext(calendar.Id, travel.Id);
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

    private static async Task SeedEventAsync(
        CalendarDbContext db, SeedContext seed, string title,
        DateTimeOffset start, DateTimeOffset end, Guid placeId, bool travel)
    {
        var ev = new Event
        {
            Id = Guid.CreateVersion7(), CalendarId = seed.CalendarId,
            RemoteId = Guid.NewGuid().ToString("N"), Uid = Guid.NewGuid().ToString("N"),
            Title = title, StartUtc = start, EndUtc = end, PlaceId = placeId,
            Status = EventStatus.Confirmed, RowVersion = Guid.NewGuid().ToString("N"),
        };
        if (travel)
        {
            var category = await db.Categories.FirstAsync(c => c.Id == seed.TravelCategoryId);
            ev.Categories.Add(category);
        }
        db.Events.Add(ev);
        await db.SaveChangesAsync();
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    private sealed class FakeRouteProvider : IRouteProvider
    {
        private readonly string _geometry;
        public int Calls { get; private set; }

        public FakeRouteProvider(string geometry)
        {
            _geometry = geometry;
            Manifest = new PluginManifest(
                "org.unifiedcalendar.fake.route", "Fake Router", "1.0", "1.x", PluginKind.Declarative,
                new[] { CapabilityIds.GeoRoute }, Publisher: null,
                new AuthSpec(AuthScheme.None), new NetworkSpec(new[] { "*" }),
                new ConfigSchema("{}", Array.Empty<string>()));
        }

        public RouteCoverage Coverage { get; } = new(true, true, true, true, false);
        public PluginManifest Manifest { get; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<RouteResult> RouteAsync(
            GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RouteResult(3600, _geometry, LeaveByUtc: null, Feasible: true, "fake"));
        }
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
