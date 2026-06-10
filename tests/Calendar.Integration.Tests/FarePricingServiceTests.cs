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
/// The host <see cref="FarePricingService"/> driving the REAL flight/stay aggregators (travel-fares-plugin.md
/// §9, ARCHITECTURE.md §14/§15) with fake pricing providers — no host, no network. Asserts the service fans
/// out, dedupes + picks cheapest + tags the winning Duffel source, degrades to a stale last-known when every
/// provider is down, and returns EMPTY (no overlay, no crash) when no provider is registered/configured.
/// </summary>
public sealed class FarePricingServiceTests
{
    private static readonly FlightSearchRequest FlightReq =
        new("JFK", "LHR", new DateOnly(2026, 8, 12), Return: null, Adults: 1);

    private static readonly StaySearchRequest StayReq =
        new(52.52, 13.405, new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 15), Adults: 2, Rooms: 1);

    private static readonly FareCoverage UsCoverage =
        new(Set("US"), Set("USD"), MultiCity: true, OneWay: true, "");

    // ── flights ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Flights_fanout_dedupes_keeps_cheapest_and_tags_duffel_source()
    {
        // Same itinerary from two providers; the cheaper "duffel" offer must win and keep its source tag.
        var duffel = new FakeFlightPricing("duffel", UsCoverage, FareOffer("duffel", "BA", "117", 412m));
        var other = new FakeFlightPricing("other", UsCoverage, FareOffer("other", "BA", "117", 530m));
        var service = ServiceWith(
            (Capability.FlightPrice, duffel),
            (Capability.FlightPrice, other));

        var result = await service.SearchFlightsAsync(FlightReq, CancellationToken.None);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(412m, offer.Price);
        Assert.Equal("duffel", offer.Source);
        Assert.False(result.Stale);
        Assert.Contains("duffel", result.Source);  // aggregator's "+"-joined winning sources.
    }

    [Fact]
    public async Task Flights_no_provider_returns_empty_no_crash()
    {
        var service = ServiceWith(); // nothing registered for flight.price.

        var result = await service.SearchFlightsAsync(FlightReq, CancellationToken.None);

        Assert.Empty(result.Offers);
        Assert.Null(result.Source);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task Flights_all_down_serves_stale_last_known()
    {
        var provider = new FakeFlightPricing("duffel", UsCoverage, FareOffer("duffel", "BA", "117", 412m));
        var registry = RegistryWith((Capability.FlightPrice, provider));
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var cache = new InMemoryAggregationResultCache(() => now);
        var service = new FarePricingService(
            new FlightPricingAggregator(registry, cache, NullLogger<FlightPricingAggregator>.Instance),
            new StayPricingAggregator(registry, cache, NullLogger<StayPricingAggregator>.Instance),
            NullLogger<FarePricingService>.Instance);

        // First call populates fresh + last-known.
        var first = await service.SearchFlightsAsync(FlightReq, CancellationToken.None);
        Assert.False(first.Stale);
        Assert.Single(first.Offers);

        // Past the freshness TTL and the provider now throws → only the stale last-known remains.
        now = now.AddHours(1);
        provider.AlwaysThrow = true;
        var degraded = await service.SearchFlightsAsync(FlightReq, CancellationToken.None);

        Assert.True(degraded.Stale);
        Assert.Single(degraded.Offers);
        Assert.Equal(412m, degraded.Offers[0].Price);
    }

    // ── stays ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stays_fanout_keeps_cheapest_and_tags_duffel_source()
    {
        var duffel = new FakeStayPricing("duffel", UsCoverage, StayOffer("duffel", 600m));
        var other = new FakeStayPricing("other", UsCoverage, StayOffer("other", 750m));
        var service = ServiceWith(
            (Capability.StayPrice, duffel),
            (Capability.StayPrice, other));

        var result = await service.SearchStaysAsync(StayReq, CancellationToken.None);

        var offer = Assert.Single(result.Offers);
        Assert.Equal(600m, offer.PriceTotal);
        Assert.Equal("duffel", offer.Source);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task Stays_no_provider_returns_empty_no_crash()
    {
        var service = ServiceWith();

        var result = await service.SearchStaysAsync(StayReq, CancellationToken.None);

        Assert.Empty(result.Offers);
        Assert.Null(result.Source);
        Assert.False(result.Stale);
    }

    // ── fakes & helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static FareOffer FareOffer(string source, string carrier, string flightNo, decimal price) =>
        new(price, "USD", "JFK", "LHR", new DateOnly(2026, 8, 12), carrier, flightNo,
            new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 6, 0, 0, TimeSpan.Zero),
            DeepLink: "https://www.duffel.com/off_1", Source: source,
            RetrievedAt: DateTimeOffset.UtcNow, Stale: false);

    private static StayOffer StayOffer(string source, decimal total) =>
        new(PricePerNight: total / 3, PriceTotal: total, Currency: "USD",
            PlaceLabel: "Hotel Berlin Mitte", Lat: 52.5219, Lng: 13.4067,
            CheckIn: new DateOnly(2026, 8, 12), CheckOut: new DateOnly(2026, 8, 15),
            DeepLink: "https://www.duffel.com/stays/res_1", Source: source,
            RetrievedAt: DateTimeOffset.UtcNow, Stale: false);

    private static FarePricingService ServiceWith(params (Capability cap, IPlugin plugin)[] entries)
    {
        var registry = RegistryWith(entries);
        var cache = new InMemoryAggregationResultCache();
        return new FarePricingService(
            new FlightPricingAggregator(registry, cache, NullLogger<FlightPricingAggregator>.Instance),
            new StayPricingAggregator(registry, cache, NullLogger<StayPricingAggregator>.Instance),
            NullLogger<FarePricingService>.Instance);
    }

    private static IPluginRegistry RegistryWith(params (Capability cap, IPlugin plugin)[] entries)
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
        return registry;
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

    private sealed class FakeFlightPricing(string id, FareCoverage coverage, params FareOffer[] offers) : IFlightPricing
    {
        public PluginManifest Manifest { get; } = FarePricingServiceTests.Manifest(id);
        public FareCoverage Coverage { get; } = coverage;
        public bool AlwaysThrow { get; set; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct)
        {
            if (AlwaysThrow)
                throw new InvalidOperationException($"{id} is down");
            return Task.FromResult<IReadOnlyList<FareOffer>>(offers);
        }
    }

    private sealed class FakeStayPricing(string id, FareCoverage coverage, params StayOffer[] offers) : IStayPricing
    {
        public PluginManifest Manifest { get; } = FarePricingServiceTests.Manifest(id);
        public FareCoverage Coverage { get; } = coverage;

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<StayOffer>> SearchAsync(StayQuery q, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StayOffer>>(offers);
    }
}
