using Calendar.Application.Aggregation;
using Calendar.Application.Fares;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Aggregation;
using Calendar.Infrastructure.Fares;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// The host <see cref="FarePricingService.BuildOverlayAsync"/> driving the REAL flight/stay aggregators with fake
/// providers (travel-fares-plugin.md §10.2, ARCHITECTURE §14). Asserts the overlay buckets the cheapest price per
/// date through the fan-out aggregator, picks the cheapest per cell, and returns an EMPTY map (no overlay, no
/// crash) when no provider is registered/configured.
/// </summary>
public sealed class FareOverlayServiceTests
{
    private static readonly FareCoverage UsCoverage =
        new(Set("US"), Set("USD"), MultiCity: true, OneWay: true, "");

    [Fact]
    public async Task Flight_overlay_buckets_cheapest_price_per_departure_date()
    {
        // A provider that prices each queried departure date (the FareQuery.DepartDate) at a per-date amount.
        var provider = new DatedFlightPricing("duffel", UsCoverage, date => date.Day * 10m);
        var service = ServiceWith((Capability.FlightPrice, provider));

        var overlay = await service.BuildOverlayAsync(
            new OverlayRequest(FareOverlayKind.Flight, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 3),
                Origin: "JFK", Dest: "LHR"),
            CancellationToken.None);

        Assert.Equal(3, overlay.Cells.Count);
        Assert.Equal(10m, overlay.Cells[new DateOnly(2026, 8, 1)].Price);
        Assert.Equal(20m, overlay.Cells[new DateOnly(2026, 8, 2)].Price);
        Assert.Equal(30m, overlay.Cells[new DateOnly(2026, 8, 3)].Price);
        Assert.Equal("duffel", overlay.Cells[new DateOnly(2026, 8, 1)].Source);
        Assert.False(overlay.Cells[new DateOnly(2026, 8, 1)].Stale);
    }

    [Fact]
    public async Task Flight_overlay_keeps_the_cheapest_across_providers_per_date()
    {
        var duffel = new DatedFlightPricing("duffel", UsCoverage, _ => 412m);
        var kiwi = new DatedFlightPricing("kiwi", UsCoverage, _ => 388m);
        var service = ServiceWith((Capability.FlightPrice, duffel), (Capability.FlightPrice, kiwi));

        var overlay = await service.BuildOverlayAsync(
            new OverlayRequest(FareOverlayKind.Flight, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 1),
                Origin: "JFK", Dest: "LHR"),
            CancellationToken.None);

        var cell = overlay.Cells[new DateOnly(2026, 8, 1)];
        Assert.Equal(388m, cell.Price);
        Assert.Equal("kiwi", cell.Source);
    }

    [Fact]
    public async Task No_provider_returns_an_empty_overlay()
    {
        var service = ServiceWith(); // nothing registered for flight.price.

        var overlay = await service.BuildOverlayAsync(
            new OverlayRequest(FareOverlayKind.Flight, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5),
                Origin: "JFK", Dest: "LHR"),
            CancellationToken.None);

        Assert.Empty(overlay.Cells);
        Assert.Null(overlay.Source);
    }

    [Fact]
    public async Task Flight_overlay_requires_origin_and_dest()
    {
        var provider = new DatedFlightPricing("duffel", UsCoverage, _ => 100m);
        var service = ServiceWith((Capability.FlightPrice, provider));

        var overlay = await service.BuildOverlayAsync(
            new OverlayRequest(FareOverlayKind.Flight, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5)),
            CancellationToken.None);

        Assert.Empty(overlay.Cells);
    }

    [Fact]
    public async Task Stay_overlay_prices_each_checkin_with_nightly_rate()
    {
        var provider = new NightlyStayPricing("duffel", UsCoverage, pricePerNight: 150m);
        var service = ServiceWith((Capability.StayPrice, provider));

        var overlay = await service.BuildOverlayAsync(
            new OverlayRequest(FareOverlayKind.Stay, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2),
                Lat: 52.52, Lng: 13.405, Nights: 2),
            CancellationToken.None);

        Assert.Equal(2, overlay.Cells.Count);
        Assert.Equal(150m, overlay.Cells[new DateOnly(2026, 8, 1)].Price);
    }

    // ── fakes & helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static FarePricingService ServiceWith(params (Capability cap, IPlugin plugin)[] entries)
    {
        var registry = new PluginRegistry();
        foreach (var (cap, plugin) in entries)
        {
            var manifest = new PluginManifest(
                plugin.Manifest.Id, plugin.Manifest.Id, "1.0.0", "1.x", PluginKind.Assembly,
                new[] { CapabilityIds.For(cap) }, Publisher: null,
                new AuthSpec(AuthScheme.None), new NetworkSpec(Array.Empty<string>()),
                new ConfigSchema("{\"type\":\"object\"}", Array.Empty<string>()));
            registry.Register(new PluginRegistration(
                manifest, new[] { cap }, PluginState.Running, new FakeInstance(plugin)));
        }

        var cache = new InMemoryAggregationResultCache();
        return new FarePricingService(
            new FlightPricingAggregator(registry, cache, NullLogger<FlightPricingAggregator>.Instance),
            new StayPricingAggregator(registry, cache, NullLogger<StayPricingAggregator>.Instance),
            NullLogger<FarePricingService>.Instance);
    }

    private sealed class FakeInstance(IPlugin plugin) : IPluginInstance
    {
        public string PluginId => plugin.Manifest.Id;
        public IPlugin? Plugin => plugin;
    }

    private static PluginManifest Manifest(string id) => new(
        id, id, "1.0.0", "1.x", PluginKind.Assembly, Array.Empty<string>(), null,
        new AuthSpec(AuthScheme.None), new NetworkSpec(Array.Empty<string>()),
        new ConfigSchema("{}", Array.Empty<string>()));

    private sealed class DatedFlightPricing(string id, FareCoverage coverage, Func<DateOnly, decimal> price) : IFlightPricing
    {
        public PluginManifest Manifest { get; } = FareOverlayServiceTests.Manifest(id);
        public FareCoverage Coverage { get; } = coverage;

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct)
        {
            var offer = new FareOffer(
                price(q.DepartDate), q.Currency, q.From, q.To, q.DepartDate, "BA", "117",
                new DateTimeOffset(q.DepartDate.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero),
                new DateTimeOffset(q.DepartDate.AddDays(1).ToDateTime(new TimeOnly(6, 0)), TimeSpan.Zero),
                DeepLink: $"https://www.duffel.com/{id}", Source: id,
                RetrievedAt: DateTimeOffset.UtcNow, Stale: false);
            return Task.FromResult<IReadOnlyList<FareOffer>>(new[] { offer });
        }
    }

    private sealed class NightlyStayPricing(string id, FareCoverage coverage, decimal pricePerNight) : IStayPricing
    {
        public PluginManifest Manifest { get; } = FareOverlayServiceTests.Manifest(id);
        public FareCoverage Coverage { get; } = coverage;

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<StayOffer>> SearchAsync(StayQuery q, CancellationToken ct)
        {
            var nights = Math.Max(1, q.CheckOut.DayNumber - q.CheckIn.DayNumber);
            var offer = new StayOffer(
                PricePerNight: pricePerNight, PriceTotal: pricePerNight * nights, Currency: q.Currency,
                PlaceLabel: "Hotel Berlin", Lat: q.Lat, Lng: q.Lng,
                CheckIn: q.CheckIn, CheckOut: q.CheckOut,
                DeepLink: $"https://www.duffel.com/stays/{id}", Source: id,
                RetrievedAt: DateTimeOffset.UtcNow, Stale: false);
            return Task.FromResult<IReadOnlyList<StayOffer>>(new[] { offer });
        }
    }
}
