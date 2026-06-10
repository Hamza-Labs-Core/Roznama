using System.Net;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Plugin.Duffel.Tests;

/// <summary>
/// The Duffel plugin against a stub HTTP API + a fake api-key broker (travel-fares-plugin.md §4/§5/§13).
/// Covers offer-request → <see cref="FareOffer"/> mapping, stays search → <see cref="StayOffer"/> mapping,
/// the broker applying the bearer + the mandatory <c>Duffel-Version: v2</c> header, empty-result degradation,
/// 401 → clear auth error, and that the bundle exposes both pricing capabilities.
/// </summary>
public class DuffelPluginTests
{
    private static readonly FareQuery RoundTrip = new(
        "JFK", "LHR", new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 19),
        Adults: 1, CabinClass.Economy, "USD", "US");

    private static readonly FareQuery OneWay = new(
        "JFK", "LHR", new DateOnly(2026, 8, 12), null,
        Adults: 1, CabinClass.Economy, "USD", "US");

    private static readonly StayQuery Stay = new(
        Lat: 52.52, Lng: 13.405, RadiusKm: 5,
        CheckIn: new DateOnly(2026, 8, 12), CheckOut: new DateOnly(2026, 8, 15),
        Adults: 2, Rooms: 1, "USD", "US");

    // ── flight.price ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Flights_maps_offers_to_FareOffers_with_source_and_deeplink()
    {
        var stub = new DuffelApiStub().On("/air/offer_requests", HttpStatusCode.Created, """
            {"data":{"id":"orq_123","offers":[
              {"id":"off_cheap","total_amount":"412.30","total_currency":"USD","owner":{"iata_code":"BA"},
               "slices":[
                 {"origin":{"iata_code":"JFK"},"destination":{"iata_code":"LHR"},
                  "segments":[{"operating_carrier_flight_number":"117","departing_at":"2026-08-12T18:30:00","arriving_at":"2026-08-13T06:25:00"}]}
               ]},
              {"id":"off_pricey","total_amount":"880.00","total_currency":"USD","owner":{"iata_code":"AA"},
               "slices":[
                 {"origin":{"iata_code":"JFK"},"destination":{"iata_code":"LHR"},
                  "segments":[{"operating_carrier_flight_number":"100","departing_at":"2026-08-12T20:00:00","arriving_at":"2026-08-13T08:00:00"}]}
               ]}
            ]}}
            """);
        var plugin = await BuildAsync(stub);

        var offers = await ((IFlightPricing)plugin).SearchAsync(OneWay, CancellationToken.None);

        Assert.Equal(2, offers.Count);
        var cheap = Assert.Single(offers, o => o.FlightNo == "117");
        Assert.Equal(412.30m, cheap.Price);
        Assert.Equal("USD", cheap.Currency);
        Assert.Equal("JFK", cheap.From);
        Assert.Equal("LHR", cheap.To);
        Assert.Equal("BA", cheap.Carrier);
        Assert.Equal(new DateOnly(2026, 8, 12), cheap.Date);
        Assert.Equal("duffel", cheap.Source);
        Assert.False(cheap.Stale);
        Assert.Contains("off_cheap", cheap.DeepLink);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 18, 30, 0, TimeSpan.Zero), cheap.DepartUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 6, 25, 0, TimeSpan.Zero), cheap.ArriveUtc);
    }

    [Fact]
    public async Task Flights_applies_bearer_and_duffel_version_header()
    {
        var stub = new DuffelApiStub().On("/air/offer_requests", HttpStatusCode.Created,
            """{"data":{"id":"orq_1","offers":[]}}""");
        var plugin = await BuildAsync(stub);

        await ((IFlightPricing)plugin).SearchAsync(OneWay, CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer duffel_test_token", request.Authorization);
        Assert.Equal("v2", request.DuffelVersion);   // omitting it is the #1 first-call failure (§3).
        Assert.Contains("return_offers=true", request.Uri.ToString());
        // Round trip would add a second slice; one-way has exactly one.
        Assert.Contains("\"slices\":[{", request.Body);
    }

    [Fact]
    public async Task Flights_round_trip_sends_two_slices()
    {
        var stub = new DuffelApiStub().On("/air/offer_requests", HttpStatusCode.Created,
            """{"data":{"id":"orq_1","offers":[]}}""");
        var plugin = await BuildAsync(stub);

        await ((IFlightPricing)plugin).SearchAsync(RoundTrip, CancellationToken.None);

        var body = Assert.Single(stub.Requests).Body!;
        Assert.Contains("\"origin\":\"JFK\",\"destination\":\"LHR\",\"departure_date\":\"2026-08-12\"", body);
        Assert.Contains("\"origin\":\"LHR\",\"destination\":\"JFK\",\"departure_date\":\"2026-08-19\"", body);
    }

    [Fact]
    public async Task Flights_empty_offers_returns_empty_not_throw()
    {
        // data.offers == [] is a degradation signal to the aggregator, not an error (§4.3).
        var stub = new DuffelApiStub().On("/air/offer_requests", HttpStatusCode.Created,
            """{"data":{"id":"orq_1","offers":[]}}""");
        var plugin = await BuildAsync(stub);

        var offers = await ((IFlightPricing)plugin).SearchAsync(OneWay, CancellationToken.None);

        Assert.Empty(offers);
    }

    [Fact]
    public async Task Flights_401_surfaces_a_clear_auth_error()
    {
        var stub = new DuffelApiStub().On("/air/offer_requests", HttpStatusCode.Unauthorized,
            """{"errors":[{"type":"authentication_error"}]}""");
        var plugin = await BuildAsync(stub);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ((IFlightPricing)plugin).SearchAsync(OneWay, CancellationToken.None));
    }

    // ── stay.price ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stays_maps_results_to_StayOffers_with_per_night_rate()
    {
        var stub = new DuffelApiStub().On("/stays/search", HttpStatusCode.OK, """
            {"data":{"results":[
              {"id":"res_1","cheapest_rate_total_amount":"600.00","cheapest_rate_currency":"USD",
               "accommodation":{"name":"Hotel Berlin Mitte",
                 "location":{"geographic_coordinates":{"latitude":52.5219,"longitude":13.4067}}}}
            ]}}
            """);
        var plugin = await BuildAsync(stub);

        var offers = await ((IStayPricing)plugin).SearchAsync(Stay, CancellationToken.None);

        var offer = Assert.Single(offers);
        Assert.Equal(600.00m, offer.PriceTotal);
        Assert.Equal(200.00m, offer.PricePerNight);  // 600 / 3 nights.
        Assert.Equal("USD", offer.Currency);
        Assert.Equal("Hotel Berlin Mitte", offer.PlaceLabel);
        Assert.Equal(52.5219, offer.Lat, 4);
        Assert.Equal(13.4067, offer.Lng, 4);
        Assert.Equal(Stay.CheckIn, offer.CheckIn);
        Assert.Equal(Stay.CheckOut, offer.CheckOut);
        Assert.Equal("duffel", offer.Source);
        Assert.False(offer.Stale);
        Assert.Contains("res_1", offer.DeepLink);
    }

    [Fact]
    public async Task Stays_sends_coordinates_radius_and_dates()
    {
        var stub = new DuffelApiStub().On("/stays/search", HttpStatusCode.OK,
            """{"data":{"results":[]}}""");
        var plugin = await BuildAsync(stub);

        await ((IStayPricing)plugin).SearchAsync(Stay, CancellationToken.None);

        var request = Assert.Single(stub.Requests);
        Assert.Equal("Bearer duffel_test_token", request.Authorization);
        Assert.Equal("v2", request.DuffelVersion);
        Assert.Contains("\"check_in_date\":\"2026-08-12\"", request.Body);
        Assert.Contains("\"check_out_date\":\"2026-08-15\"", request.Body);
        Assert.Contains("\"latitude\":52.52", request.Body);
        Assert.Contains("\"radius\":5", request.Body);
    }

    [Fact]
    public async Task Stays_empty_results_returns_empty_not_throw()
    {
        var stub = new DuffelApiStub().On("/stays/search", HttpStatusCode.OK,
            """{"data":{"results":[]}}""");
        var plugin = await BuildAsync(stub);

        var offers = await ((IStayPricing)plugin).SearchAsync(Stay, CancellationToken.None);

        Assert.Empty(offers);
    }

    // ── manifest / capabilities ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_declares_both_pricing_capabilities_and_apikey_auth()
    {
        var plugin = new DuffelPlugin();

        Assert.Contains(CapabilityIds.FlightPrice, plugin.Manifest.Capabilities);
        Assert.Contains(CapabilityIds.StayPrice, plugin.Manifest.Capabilities);
        Assert.Equal(AuthScheme.ApiKey, plugin.Manifest.Auth.Scheme);
        Assert.Equal("Authorization", plugin.Manifest.Auth.Name);
        Assert.Equal("Bearer {token}", plugin.Manifest.Auth.Format);
        Assert.Contains("api.duffel.com", plugin.Manifest.Network.Allow);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<DuffelPlugin> BuildAsync(DuffelApiStub stub)
    {
        var client = new HttpClient(stub);
        var host = new PluginHostServices(
            NullLogger.Instance,
            new FakeApiKeyBroker(),
            new InMemoryPluginCache(),
            () => client,
            configJson: """{"environment":"test","market":"US","currency":"USD","radiusKm":5}""");

        var plugin = new DuffelPlugin();
        await plugin.InitializeAsync(host, CancellationToken.None);
        return plugin;
    }
}
