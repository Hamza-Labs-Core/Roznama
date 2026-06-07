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
using SdkPlace = Calendar.Plugin.Abstractions.Place;

namespace Calendar.Integration.Tests;

/// <summary>
/// The host geocoding pipeline (ARCHITECTURE §7, geo-geocoding-places-plugin.md §3): event Location text →
/// a deduped <see cref="Place"/> pin via the <c>geo.geocode</c> aggregator, cached so an identical query
/// makes zero outbound calls, with same-address events collapsing to one pin. No live network — a fake
/// <see cref="IGeocoder"/> stands in for Nominatim and counts its calls.
/// </summary>
public sealed class GeocodeServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-geo-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task A_cache_hit_resolves_without_a_second_outbound_call()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var geocoder = new CountingGeocoder(("brandenburg gate, berlin", new SdkPlace(52.51627, 13.37770, "Brandenburg Gate", "Brandenburg Gate, Berlin", "fake")));
        var calendarId = await SeedCalendarAsync(db);
        // Two distinct events, same location text → second must hit the cache.
        await SeedEventAsync(db, calendarId, "Concert", "Brandenburg Gate, Berlin");
        await SeedEventAsync(db, calendarId, "Tour", "  BRANDENBURG  Gate,  Berlin ");

        var service = BuildService(db, geocoder);
        var resolved = await service.GeocodePendingEventsAsync(calendarId, CancellationToken.None);

        Assert.Equal(2, resolved);
        Assert.Equal(1, geocoder.Calls); // the normalized query is identical → one outbound call, one cache row.
        Assert.Single(await db.GeocodeCache.ToListAsync());
    }

    [Fact]
    public async Task Same_address_events_collapse_to_one_Place_pin()
    {
        using var db = NewDb();
        db.Database.Migrate();

        // Same coordinates+label returned for two different query strings → one deduped Place.
        var geocoder = new CountingGeocoder(
            ("eiffel tower", new SdkPlace(48.85837, 2.29448, "Eiffel Tower", "Eiffel Tower, Paris", "fake")),
            ("tour eiffel", new SdkPlace(48.85837, 2.29448, "Eiffel Tower", "Eiffel Tower, Paris", "fake")));

        var calendarId = await SeedCalendarAsync(db);
        var a = await SeedEventAsync(db, calendarId, "Meet", "Eiffel Tower");
        var b = await SeedEventAsync(db, calendarId, "Lunch", "Tour Eiffel");

        var service = BuildService(db, geocoder);
        await service.GeocodePendingEventsAsync(calendarId, CancellationToken.None);

        Assert.Single(await db.Places.ToListAsync()); // one pin for the shared address.
        var evA = await db.Events.AsNoTracking().FirstAsync(e => e.Id == a);
        var evB = await db.Events.AsNoTracking().FirstAsync(e => e.Id == b);
        Assert.NotNull(evA.PlaceId);
        Assert.Equal(evA.PlaceId, evB.PlaceId); // both events point at the same Place row.
    }

    [Fact]
    public async Task No_geo_provider_registered_is_a_graceful_no_op()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var calendarId = await SeedCalendarAsync(db);
        await SeedEventAsync(db, calendarId, "Concert", "Brandenburg Gate, Berlin");

        // Empty registry → no IGeocoder under geo.geocode.
        var registry = new PluginRegistry();
        var aggregator = new GeocodeAggregator(registry, new InMemoryAggregationResultCache(),
            NullLogger<GeocodeAggregator>.Instance);
        var service = new GeocodeService(db, aggregator, registry, new DeviceProvider(db),
            NullLogger<GeocodeService>.Instance);

        var resolved = await service.GeocodePendingEventsAsync(calendarId, CancellationToken.None);

        Assert.Equal(0, resolved);
        Assert.Empty(await db.Places.ToListAsync());
        Assert.Empty(await db.GeocodeCache.ToListAsync());
        var ev = await db.Events.AsNoTracking().FirstAsync();
        Assert.Null(ev.PlaceId);                       // event keeps its raw text, no pin.
        Assert.Equal("Brandenburg Gate, Berlin", ev.Location);
    }

    [Fact]
    public async Task A_query_with_no_hit_leaves_the_event_ungeocoded_and_writes_no_empty_Place()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var geocoder = new CountingGeocoder(); // resolves nothing.
        var calendarId = await SeedCalendarAsync(db);
        await SeedEventAsync(db, calendarId, "Mystery", "nowhere at all 123xyz");

        var service = BuildService(db, geocoder);
        var resolved = await service.GeocodePendingEventsAsync(calendarId, CancellationToken.None);

        Assert.Equal(0, resolved);
        Assert.Empty(await db.Places.ToListAsync());
        Assert.Null((await db.Events.AsNoTracking().FirstAsync()).PlaceId);
    }

    [Fact]
    public async Task Map_events_projection_carries_coordinates_for_geocoded_events()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var geocoder = new CountingGeocoder(
            ("brandenburg gate, berlin", new SdkPlace(52.51627, 13.37770, "Brandenburg Gate", "Brandenburg Gate, Berlin", "fake")));
        var calendarId = await SeedCalendarAsync(db, color: "#ff8800");
        await SeedEventAsync(db, calendarId, "Concert", "Brandenburg Gate, Berlin",
            start: new DateTimeOffset(2026, 6, 10, 19, 0, 0, TimeSpan.Zero));
        await SeedEventAsync(db, calendarId, "No location", location: null);

        await BuildService(db, geocoder).GeocodePendingEventsAsync(calendarId, CancellationToken.None);

        var map = new MapViewService(db);
        var pins = await map.GetMapEventsAsync(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);

        var pin = Assert.Single(pins);                 // only the geocoded event becomes a pin.
        Assert.Equal("Concert", pin.Title);
        Assert.Equal(52.51627, pin.Lat, precision: 5);
        Assert.Equal(13.37770, pin.Lng, precision: 5);
        Assert.Equal("#ff8800", pin.Color);            // pin inherits the calendar color.

        var places = await map.ListPlacesAsync(CancellationToken.None);
        Assert.Single(places);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static GeocodeService BuildService(CalendarDbContext db, CountingGeocoder geocoder)
    {
        var registry = new PluginRegistry();
        registry.Register(new PluginRegistration(
            geocoder.Manifest,
            new[] { Capability.GeoGeocode },
            PluginState.Running,
            new TestInstance(geocoder.Manifest.Id, geocoder)));

        var aggregator = new GeocodeAggregator(registry, new InMemoryAggregationResultCache(),
            NullLogger<GeocodeAggregator>.Instance);
        return new GeocodeService(db, aggregator, registry, new DeviceProvider(db),
            NullLogger<GeocodeService>.Instance);
    }

    private static async Task<Guid> SeedCalendarAsync(CalendarDbContext db, string? color = null)
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
            Color = color, IsVisible = true, UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        };
        db.Plugins.Add(plugin);
        db.Accounts.Add(account);
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar.Id;
    }

    private static async Task<Guid> SeedEventAsync(
        CalendarDbContext db, Guid calendarId, string title, string? location,
        DateTimeOffset? start = null)
    {
        var s = start ?? new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero);
        var ev = new Event
        {
            Id = Guid.CreateVersion7(),
            CalendarId = calendarId,
            RemoteId = Guid.NewGuid().ToString("N"),
            Uid = Guid.NewGuid().ToString("N"),
            Title = title,
            StartUtc = s,
            EndUtc = s.AddHours(2),
            Location = location,
            Status = EventStatus.Confirmed,
            RowVersion = Guid.NewGuid().ToString("N"),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    /// <summary>A fake <see cref="IGeocoder"/> that answers from a fixed table and counts outbound calls.</summary>
    private sealed class CountingGeocoder : IGeocoder
    {
        private readonly IReadOnlyDictionary<string, SdkPlace> _table;
        public int Calls { get; private set; }

        public CountingGeocoder(params (string NormalizedQuery, SdkPlace Place)[] entries) =>
            _table = entries.ToDictionary(e => e.NormalizedQuery, e => e.Place, StringComparer.Ordinal);

        public PluginManifest Manifest { get; } = new(
            "org.unifiedcalendar.fake.geocoder", "Fake Geocoder", "1.0", "1.x", PluginKind.Declarative,
            new[] { CapabilityIds.GeoGeocode }, Publisher: null,
            new AuthSpec(AuthScheme.None), new NetworkSpec(new[] { "*" }), new ConfigSchema("{}", Array.Empty<string>()));

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<SdkPlace?> GeocodeAsync(string query, GeoBias? bias, CancellationToken ct)
        {
            Calls++;
            var key = string.Join(' ', query.Trim().ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return Task.FromResult(_table.TryGetValue(key, out var place) ? place : null);
        }

        public Task<SdkPlace?> ReverseGeocodeAsync(GeoPoint point, CancellationToken ct) =>
            Task.FromResult<SdkPlace?>(null);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
