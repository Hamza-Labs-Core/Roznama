using Calendar.Application.Fares;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Infrastructure.Aggregation;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Fares;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// The host fare-watch pipeline (ARCHITECTURE §14, travel-fares-plugin.md §10) over a temp SQLite store and a
/// fake flight-pricing provider — no host, no network. Asserts a watch records a sample per poll, that a drop
/// below the threshold (and a target cross) fires EXACTLY ONE notification while no-change/price-rise stay
/// silent, that history returns newest-first, and that delete stops polling. Empty-provider polls record nothing
/// and never throw.
/// </summary>
public sealed class FareWatchServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-farewatch-{Guid.NewGuid():N}.db");
    private DateTimeOffset _now = new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly FareCoverage UsCoverage =
        new(Set("US"), Set("USD"), MultiCity: true, OneWay: true, "");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    // ── samples accumulate over polls ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_poll_records_a_sample()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, _) = BuildService(db, provider);

        var watch = await service.CreateAsync(FlightWatch(), CancellationToken.None);

        await PollAfterTick(service);
        provider.Price = 470m;
        await PollAfterTick(service);
        provider.Price = 460m;
        await PollAfterTick(service);

        var history = await service.GetHistoryAsync(watch.Id, CancellationToken.None);
        Assert.Equal(3, history.Count);
        Assert.All(history, s => Assert.Equal("duffel", s.Source));
    }

    [Fact]
    public async Task History_is_newest_first()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, _) = BuildService(db, provider);
        var watch = await service.CreateAsync(FlightWatch(), CancellationToken.None);

        await PollAfterTick(service);     // 480, earlier
        provider.Price = 500m;
        await PollAfterTick(service);     // 500, later

        var history = await service.GetHistoryAsync(watch.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.True(history[0].SampledAtUtc > history[1].SampledAtUtc);
        Assert.Equal(500m, history[0].Price); // newest first
    }

    // ── drop / target firing ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_drop_below_threshold_fires_exactly_one_notification()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, notifications) = BuildService(db, provider);
        // 10% threshold; 480 → 410 is ~14.6% — a firing drop.
        var watch = await service.CreateAsync(FlightWatch(dropThreshold: 0.10), CancellationToken.None);

        await PollAfterTick(service); // first sample: 480, establishes the low — no fire.
        provider.Price = 410m;
        await PollAfterTick(service); // drop → one notification.
        await PollAfterTick(service); // same 410 → no further notification (debounced).

        var fired = await notifications.ListAsync(100, CancellationToken.None);
        var one = Assert.Single(fired);
        Assert.Equal(NotificationKind.FareDrop, one.Kind);
        Assert.Equal(410m, one.Price);
        Assert.Equal(480m, one.PreviousPrice);
        Assert.Equal("duffel", one.Source);
        Assert.True(one.Delivered);
    }

    [Fact]
    public async Task A_small_drop_below_threshold_does_not_fire()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, notifications) = BuildService(db, provider);
        var watch = await service.CreateAsync(FlightWatch(dropThreshold: 0.10), CancellationToken.None);

        await PollAfterTick(service); // 480 baseline.
        provider.Price = 470m;        // ~2% drop < 10% threshold.
        await PollAfterTick(service);

        Assert.Empty(await notifications.ListAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task A_price_rise_does_not_fire()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, notifications) = BuildService(db, provider);
        await service.CreateAsync(FlightWatch(dropThreshold: 0.10), CancellationToken.None);

        await PollAfterTick(service); // 480 baseline.
        provider.Price = 600m;
        await PollAfterTick(service);

        Assert.Empty(await notifications.ListAsync(100, CancellationToken.None));
    }

    [Fact]
    public async Task Crossing_the_target_fires_exactly_one_notification()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, notifications) = BuildService(db, provider);
        // No drop threshold relevance: target 420; price falls under it once.
        await service.CreateAsync(FlightWatch(targetPrice: 420m, dropThreshold: 0.50), CancellationToken.None);

        await PollAfterTick(service); // 480, above target — no fire.
        provider.Price = 415m;
        await PollAfterTick(service); // crosses under target → one fire.
        provider.Price = 410m;
        await PollAfterTick(service); // still under target → no re-fire.

        var one = Assert.Single(await notifications.ListAsync(100, CancellationToken.None));
        Assert.Equal(NotificationKind.FareTarget, one.Kind);
        Assert.Equal(415m, one.Price);
    }

    // ── delete stops polling ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_stops_polling()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var provider = new MutableFlightPricing("duffel", UsCoverage, 480m);
        var (service, _) = BuildService(db, provider);
        var watch = await service.CreateAsync(FlightWatch(), CancellationToken.None);

        await PollAfterTick(service);
        Assert.Single(await service.GetHistoryAsync(watch.Id, CancellationToken.None));

        Assert.True(await service.DeleteAsync(watch.Id, CancellationToken.None));
        Assert.Empty(await service.ListAsync(CancellationToken.None));

        var summary = await PollAfterTick(service); // deleted watch must not be polled.
        Assert.Equal(0, summary.WatchesPolled);
        Assert.Single(await service.GetHistoryAsync(watch.Id, CancellationToken.None)); // unchanged.
    }

    [Fact]
    public async Task Delete_unknown_returns_false()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (service, _) = BuildService(db, new MutableFlightPricing("duffel", UsCoverage, 480m));
        Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ── graceful degradation: no provider ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_provider_poll_records_nothing_and_does_not_throw()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (service, notifications) = BuildService(db); // no pricing provider registered.
        var watch = await service.CreateAsync(FlightWatch(), CancellationToken.None);

        var summary = await service.PollAsync(CancellationToken.None);

        Assert.Equal(1, summary.WatchesPolled);
        Assert.Equal(0, summary.SamplesRecorded);
        Assert.Equal(0, summary.NotificationsFired);
        Assert.Empty(await service.GetHistoryAsync(watch.Id, CancellationToken.None));
        Assert.Empty(await notifications.ListAsync(100, CancellationToken.None));
    }

    // ── helpers & fakes ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advance the clock past the aggregator's freshness TTL (5 min) so the next poll re-queries the provider
    /// instead of serving the cached price — modelling real polls that are spaced well apart.
    /// </summary>
    private async Task<PollSummary> PollAfterTick(FareWatchService service)
    {
        _now = _now.AddMinutes(10);
        return await service.PollAsync(CancellationToken.None);
    }

    private CreateFareWatchRequest FlightWatch(decimal? targetPrice = null, double? dropThreshold = null) =>
        new(FareKind.Flight, new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 19),
            Pax: 1, Currency: "USD", TargetPrice: targetPrice, DropThreshold: dropThreshold,
            OriginIata: "JFK", DestIata: "LHR");

    private (FareWatchService Service, INotificationReader Notifications) BuildService(
        CalendarDbContext db, params IPlugin[] providers)
    {
        var registry = new PluginRegistry();
        foreach (var p in providers)
        {
            var manifest = new PluginManifest(
                p.Manifest.Id, p.Manifest.Id, "1.0.0", "1.x", PluginKind.Assembly,
                new[] { CapabilityIds.For(Capability.FlightPrice) }, Publisher: null,
                new AuthSpec(AuthScheme.None), new NetworkSpec(Array.Empty<string>()),
                new ConfigSchema("{\"type\":\"object\"}", Array.Empty<string>()));
            registry.Register(new PluginRegistration(
                manifest, new[] { Capability.FlightPrice }, PluginState.Running, new FakeInstance(p)));
        }

        var cache = new InMemoryAggregationResultCache(() => _now);
        var flights = new FlightPricingAggregator(registry, cache, NullLogger<FlightPricingAggregator>.Instance);
        var stays = new StayPricingAggregator(registry, cache, NullLogger<StayPricingAggregator>.Instance);
        var notifier = new InAppNotifier(db, () => _now);
        var device = new DeviceProvider(db);
        var service = new FareWatchService(
            db, flights, stays, new IFareNotifier[] { notifier }, device,
            NullLogger<FareWatchService>.Instance, () => _now);
        return (service, notifier);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private sealed class FakeInstance(IPlugin plugin) : IPluginInstance
    {
        public string PluginId => plugin.Manifest.Id;
        public IPlugin? Plugin => plugin;
    }

    /// <summary>A flight-pricing fake whose single offer's price can change between polls.</summary>
    private sealed class MutableFlightPricing(string id, FareCoverage coverage, decimal price) : IFlightPricing
    {
        public PluginManifest Manifest { get; } = new(
            id, id, "1.0.0", "1.x", PluginKind.Assembly, Array.Empty<string>(), null,
            new AuthSpec(AuthScheme.None), new NetworkSpec(Array.Empty<string>()),
            new ConfigSchema("{}", Array.Empty<string>()));
        public FareCoverage Coverage { get; } = coverage;
        public decimal Price { get; set; } = price;

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct)
        {
            var offer = new FareOffer(
                Price, "USD", "JFK", "LHR", new DateOnly(2026, 8, 12), "BA", "117",
                new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 13, 6, 0, 0, TimeSpan.Zero),
                DeepLink: "https://www.duffel.com/off_1", Source: id,
                RetrievedAt: DateTimeOffset.UtcNow, Stale: false);
            return Task.FromResult<IReadOnlyList<FareOffer>>(new[] { offer });
        }
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* temp file */ }
    }
}
