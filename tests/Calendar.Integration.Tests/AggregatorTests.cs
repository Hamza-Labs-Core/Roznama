using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Aggregation;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// The multi-provider fallback aggregator (ARCHITECTURE.md §15, PLUGIN-HOST.md §9) exercised with FAKE
/// in-memory plugins — no host, no network. Asserts failover picks the first healthy provider, a
/// throwing/empty provider is skipped, fan-out dedupes + picks best + tags Source, an all-down query
/// degrades to a stale last-known, and coverage filtering skips ineligible providers.
/// </summary>
public sealed class AggregatorTests
{
    // ---- geo.route: failover, skip-on-throw, coverage filter, fan-out fastest ----

    [Fact]
    public async Task Route_failover_picks_first_healthy_provider()
    {
        var registry = RegistryWith(
            (Capability.GeoRoute, new FakeRouteProvider("osrm", DriveOnly, durationSec: 600)),
            (Capability.GeoRoute, new FakeRouteProvider("google", AllModes, durationSec: 999)));

        var agg = NewRouteAggregator(registry);
        var result = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);

        Assert.True(result.HasValue);
        Assert.Equal("osrm", result.WinningSource);
        Assert.Equal(600, result.Value!.DurationSec);
        Assert.Equal("osrm", result.Value.Source);
        Assert.False(result.Stale);
    }

    [Fact]
    public async Task Route_failover_skips_a_throwing_provider()
    {
        var registry = RegistryWith(
            (Capability.GeoRoute, new FakeRouteProvider("flaky", AllModes, throws: true)),
            (Capability.GeoRoute, new FakeRouteProvider("osrm", DriveOnly, durationSec: 540)));

        var agg = NewRouteAggregator(registry);
        var result = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);

        Assert.Equal("osrm", result.WinningSource);
        Assert.Equal(540, result.Value!.DurationSec);
        Assert.Contains(result.Attempts, a => a.PluginId == "flaky" && a.Outcome == ProviderOutcome.Faulted);
        Assert.Contains(result.Attempts, a => a.PluginId == "osrm" && a.Outcome == ProviderOutcome.Success);
    }

    [Fact]
    public async Task Route_coverage_filtering_skips_a_provider_that_cannot_serve_transit()
    {
        // OSRM has no transit; a transit query must skip it (before any call) and fail over to Google.
        var osrm = new FakeRouteProvider("osrm", DriveOnly, durationSec: 600);
        var google = new FakeRouteProvider("google", AllModes, durationSec: 1200);
        var registry = RegistryWith(
            (Capability.GeoRoute, osrm),
            (Capability.GeoRoute, google));

        var agg = NewRouteAggregator(registry);
        var result = await agg.RouteAsync(A, B, TravelMode.Transit, When, AggregationOptions.Default, default);

        Assert.Equal("google", result.WinningSource);
        Assert.False(osrm.WasCalled); // skipped before the call.
        Assert.Contains(result.Attempts, a => a.PluginId == "osrm" && a.Outcome == ProviderOutcome.SkippedCoverage);
    }

    [Fact]
    public async Task Route_fanout_picks_the_fastest_route_and_tags_source()
    {
        var registry = RegistryWith(
            (Capability.GeoRoute, new FakeRouteProvider("slow", AllModes, durationSec: 1800)),
            (Capability.GeoRoute, new FakeRouteProvider("fast", AllModes, durationSec: 900)),
            (Capability.GeoRoute, new FakeRouteProvider("mid", AllModes, durationSec: 1200)));

        var agg = NewRouteAggregator(registry);
        var result = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.FanOut, default);

        Assert.Equal(900, result.Value!.DurationSec);
        Assert.Equal("fast", result.Value.Source); // winning provider stamped onto the chosen route.
    }

    [Fact]
    public async Task Route_leaves_host_computed_fields_at_provider_defaults()
    {
        var registry = RegistryWith((Capability.GeoRoute, new FakeRouteProvider("osrm", DriveOnly, durationSec: 600)));
        var agg = NewRouteAggregator(registry);

        var result = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);

        // LeaveByUtc/Feasible are host-computed against the inter-event gap — the aggregator must not set them.
        Assert.Null(result.Value!.LeaveByUtc);
        Assert.True(result.Value.Feasible);
    }

    // ---- graceful degradation: all-down → stale last-known ----

    [Fact]
    public async Task Route_all_down_serves_stale_last_known()
    {
        var good = new FakeRouteProvider("osrm", DriveOnly, durationSec: 720);
        var registry = RegistryWith((Capability.GeoRoute, good));
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var cache = new InMemoryAggregationResultCache(() => now);
        var agg = new RouteAggregator(registry, cache, NullLogger<RouteAggregator>.Instance);

        // First call succeeds and populates fresh + last-known.
        var first = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);
        Assert.False(first.Stale);
        Assert.Equal(720, first.Value!.DurationSec);

        // Advance past the freshness TTL so the fresh cache no longer short-circuits, and the provider is
        // now down for every call — the only thing left is the stale last-known.
        now = now.AddHours(1);
        good.AlwaysThrow = true;
        var degraded = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);

        Assert.True(degraded.Stale);
        Assert.True(degraded.HasValue);
        Assert.Equal(720, degraded.Value!.DurationSec); // last-known good value returned.
    }

    [Fact]
    public async Task Route_all_down_with_no_history_is_empty()
    {
        var registry = RegistryWith((Capability.GeoRoute, new FakeRouteProvider("osrm", DriveOnly, throws: true)));
        var agg = NewRouteAggregator(registry);

        var result = await agg.RouteAsync(A, B, TravelMode.Drive, When, AggregationOptions.Default, default);

        Assert.False(result.HasValue);
        Assert.False(result.Stale);
    }

    // ---- geo.geocode: failover past empty, fresh cache hit ----

    [Fact]
    public async Task Geocode_failover_skips_an_empty_provider()
    {
        var registry = RegistryWith(
            (Capability.GeoGeocode, new FakeGeocoder("empty", result: null)),
            (Capability.GeoGeocode, new FakeGeocoder("nominatim", new Place(48.85, 2.35, "Paris", "Paris, France", "nominatim"))));

        var agg = new GeocodeAggregator(registry, new InMemoryAggregationResultCache(), NullLogger<GeocodeAggregator>.Instance);
        var result = await agg.GeocodeAsync("Paris", bias: null, AggregationOptions.Default, default);

        Assert.True(result.HasValue);
        Assert.Equal("nominatim", result.WinningSource);
        Assert.Equal("Paris", result.Value!.Label);
        Assert.Contains(result.Attempts, a => a.PluginId == "empty" && a.Outcome == ProviderOutcome.Empty);
    }

    [Fact]
    public async Task Geocode_second_call_is_served_from_fresh_cache()
    {
        var provider = new FakeGeocoder("nominatim", new Place(48.85, 2.35, "Paris", "Paris, France", "nominatim"));
        var registry = RegistryWith((Capability.GeoGeocode, provider));
        var agg = new GeocodeAggregator(registry, new InMemoryAggregationResultCache(), NullLogger<GeocodeAggregator>.Instance);

        await agg.GeocodeAsync("Paris", null, AggregationOptions.Default, default);
        var second = await agg.GeocodeAsync("Paris", null, AggregationOptions.Default, default);

        Assert.True(second.FromCache);
        Assert.Equal(1, provider.CallCount); // the provider was hit only once.
    }

    // ---- flight.price: coverage filter, fan-out dedupe + cheapest ----

    [Fact]
    public async Task Flights_coverage_filtering_skips_a_provider_outside_the_market()
    {
        var euOnly = new FakeFlightPricing("kiwi",
            new FareCoverage(Set("DE", "FR"), Set("EUR"), MultiCity: false, OneWay: true, ""),
            Offer("kiwi", "BA", "117", 250m));
        var usServing = new FakeFlightPricing("duffel",
            new FareCoverage(Set("US", "DE"), Set("USD", "EUR"), MultiCity: true, OneWay: true, ""),
            Offer("duffel", "BA", "117", 300m));
        var registry = RegistryWith(
            (Capability.FlightPrice, euOnly),
            (Capability.FlightPrice, usServing));

        var agg = new FlightPricingAggregator(registry, new InMemoryAggregationResultCache(), NullLogger<FlightPricingAggregator>.Instance);
        var query = new FareQuery("JFK", "LHR", new DateOnly(2026, 7, 1), null, 1, CabinClass.Economy, "USD", "US");
        var result = await agg.SearchAsync(query, AggregationOptions.FanOut, default);

        // kiwi serves neither US market nor USD → skipped; only duffel's offer survives.
        Assert.False(euOnly.WasCalled);
        Assert.Contains(result.Attempts, a => a.PluginId == "kiwi" && a.Outcome == ProviderOutcome.SkippedCoverage);
        var offer = Assert.Single(result.Value!);
        Assert.Equal("duffel", offer.Source);
    }

    [Fact]
    public async Task Flights_fanout_dedupes_identical_itineraries_and_keeps_cheapest()
    {
        var cov = new FareCoverage(Set("US"), Set("USD"), MultiCity: true, OneWay: true, "");
        // Same Carrier+FlightNo+times from two providers at different prices, plus a distinct itinerary.
        var duffel = new FakeFlightPricing("duffel", cov,
            Offer("duffel", "BA", "117", 320m),
            Offer("duffel", "AF", "1680", 410m));
        var kiwi = new FakeFlightPricing("kiwi", cov,
            Offer("kiwi", "BA", "117", 295m)); // cheaper duplicate of the BA117 itinerary.
        var registry = RegistryWith(
            (Capability.FlightPrice, duffel),
            (Capability.FlightPrice, kiwi));

        var agg = new FlightPricingAggregator(registry, new InMemoryAggregationResultCache(), NullLogger<FlightPricingAggregator>.Instance);
        var query = new FareQuery("JFK", "LHR", new DateOnly(2026, 7, 1), null, 1, CabinClass.Economy, "USD", "US");
        var result = await agg.SearchAsync(query, AggregationOptions.FanOut, default);

        Assert.Equal(2, result.Value!.Count); // BA117 deduped to one; AF1680 kept.
        var ba = Assert.Single(result.Value, o => o.FlightNo == "117");
        Assert.Equal(295m, ba.Price);    // cheapest of the duplicates kept.
        Assert.Equal("kiwi", ba.Source); // winning provider's tag survives.
        Assert.Equal(295m, result.Value[0].Price); // sorted cheapest-first.
    }

    // ---------------- fakes & helpers ----------------

    private static readonly GeoPoint A = new(40.64, -73.78);
    private static readonly GeoPoint B = new(51.47, -0.46);
    private static readonly DateTimeOffset When = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly RouteCoverage DriveOnly = new(Drive: true, Transit: false, Walk: true, Bike: true, TrafficAware: false);
    private static readonly RouteCoverage AllModes = new(Drive: true, Transit: true, Walk: true, Bike: true, TrafficAware: true);

    private static RouteAggregator NewRouteAggregator(IPluginRegistry registry) =>
        new(registry, new InMemoryAggregationResultCache(), NullLogger<RouteAggregator>.Instance);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static FareOffer Offer(string source, string carrier, string flightNo, decimal price) =>
        new(price, "USD", "JFK", "LHR", new DateOnly(2026, 7, 1), carrier, flightNo,
            new DateTimeOffset(2026, 7, 1, 18, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero),
            DeepLink: "https://book.test", Source: source, RetrievedAt: DateTimeOffset.UtcNow, Stale: false);

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

    private sealed class FakeRouteProvider(string id, RouteCoverage coverage, int durationSec = 600, bool throws = false)
        : IRouteProvider
    {
        public PluginManifest Manifest { get; } = AggregatorTests.Manifest(id);
        public RouteCoverage Coverage { get; } = coverage;
        public bool WasCalled { get; private set; }
        public bool AlwaysThrow { get; set; } = throws;

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<RouteResult> RouteAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct)
        {
            WasCalled = true;
            if (AlwaysThrow)
                throw new InvalidOperationException($"{id} is down");
            // Provider returns DurationSec/Geometry only; LeaveByUtc null + Feasible true (host-computed).
            return Task.FromResult(new RouteResult(durationSec, Geometry: null, LeaveByUtc: null, Feasible: true, Source: id));
        }
    }

    private sealed class FakeGeocoder(string id, Place? result) : IGeocoder
    {
        public PluginManifest Manifest { get; } = AggregatorTests.Manifest(id);
        public int CallCount { get; private set; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<Place?> GeocodeAsync(string query, GeoBias? bias, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public Task<Place?> ReverseGeocodeAsync(GeoPoint point, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeFlightPricing(string id, FareCoverage coverage, params FareOffer[] offers) : IFlightPricing
    {
        public PluginManifest Manifest { get; } = AggregatorTests.Manifest(id);
        public FareCoverage Coverage { get; } = coverage;
        public bool WasCalled { get; private set; }

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult<IReadOnlyList<FareOffer>>(offers);
        }
    }
}
