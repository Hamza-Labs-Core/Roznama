using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Duffel;

/// <summary>
/// The Duffel pricing plugin (docs/deep-dives/travel-fares-plugin.md §4/§5, ARCHITECTURE.md §14): a single
/// assembly bundle implementing BOTH interchangeable pricing capabilities — <c>flight.price</c>
/// (<see cref="IFlightPricing"/>) and <c>stay.price</c> (<see cref="IStayPricing"/>) — over Duffel's versioned
/// REST/JSON API. Both ride the host's multi-provider fallback aggregator (ARCHITECTURE.md §15), which fans
/// out, dedupes, and picks the cheapest across whoever is installed; this plugin only normalizes one
/// provider's payload onto <see cref="FareOffer"/>/<see cref="StayOffer"/>.
///
/// <para>The shopping model is <b>offer-request → offers</b>: <c>POST /air/offer_requests?return_offers=true</c>
/// returns all offers inlined (best for our overlay use). Stays is a single <c>POST /stays/search</c> whose
/// results each carry a <c>cheapest_rate_total_amount</c> — enough for the nightly-rate overlay without
/// walking rates → quote → book (booking is an explicit opt-in, out of scope for the read-only pricing
/// capability).</para>
///
/// <para>The plugin never sees a credential: it builds each request, calls
/// <c>IPluginHost.Auth.ApplyAsync</c> for a fresh <c>Authorization: Bearer &lt;token&gt;</c>, and adds the
/// mandatory <c>Duffel-Version: v2</c> header (omitting it is the #1 first-call failure — §3). It talks raw
/// REST/JSON over the host's egress-filtered <see cref="HttpClient"/> so the whole path is stub-testable with
/// no live network.</para>
/// </summary>
public sealed class DuffelPlugin : IFlightPricing, IStayPricing
{
    public const string Id = "org.unifiedcalendar.duffel";

    /// <summary>The winning <see cref="FareOffer.Source"/>/<see cref="StayOffer.Source"/> tag (travel-fares-plugin.md §8).</summary>
    public const string SourceTag = "duffel";

    private const string ApiBase = "https://api.duffel.com";
    private const string DuffelVersion = "v2";

    private IPluginHost _host = default!;
    private DuffelConfig _config = default!;
    private FareCoverage _coverage = default!;

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "Duffel",
        Version: "0.1.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.FlightPrice, CapabilityIds.StayPrice },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(
            Scheme: AuthScheme.ApiKey,
            In: "header",
            Name: "Authorization",
            Format: "Bearer {token}"),
        Network: new NetworkSpec(new[] { "api.duffel.com" }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"environment":{"type":"string","enum":["test","live"],"default":"test"},"market":{"type":"string","default":"US"},"currency":{"type":"string","default":"USD"},"radiusKm":{"type":"integer","default":5}},"required":["environment"]}""",
            new[] { "environment" }));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        _config = host.GetConfig<DuffelConfig>();
        // Duffel serves 300+ airlines and millions of properties across major markets/currencies. We declare
        // the configured market/currency so the aggregator never fans a query out that we can't price; an
        // empty set would advertise "serves everything" (travel-fares-plugin.md §2 Coverage).
        _coverage = new FareCoverage(
            Markets: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _config.Market },
            Currencies: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _config.Currency },
            MultiCity: true,
            OneWay: true,
            Notes: $"Duffel ({_config.Environment}); offer-request shop flow, search-only Stays.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    FareCoverage IFlightPricing.Coverage => _coverage;

    /// <inheritdoc />
    FareCoverage IStayPricing.Coverage => _coverage;

    // ── flight.price ────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var body = BuildOfferRequestBody(q);
        var url = $"{ApiBase}/air/offer_requests?return_offers=true";

        using var doc = await PostJsonAsync(client, url, body, ct).ConfigureAwait(false);
        var retrievedAt = DateTimeOffset.UtcNow;

        // ?return_offers=true inlines every offer inside the offer-request resource.
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("offers", out var offers) ||
            offers.ValueKind != JsonValueKind.Array)
        {
            // Empty/partial is a degradation signal to the aggregator, not an error (travel-fares-plugin.md §4.3).
            return Array.Empty<FareOffer>();
        }

        var results = new List<FareOffer>();
        foreach (var offer in offers.EnumerateArray())
        {
            var mapped = DuffelNormalizer.NormalizeFareOffer(offer, q, retrievedAt);
            if (mapped is not null)
                results.Add(mapped);
        }
        return results;
    }

    // ── stay.price ──────────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<StayOffer>> SearchAsync(StayQuery q, CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var radiusKm = q.RadiusKm > 0 ? q.RadiusKm : _config.RadiusKm;
        var body = BuildStaySearchBody(q, radiusKm);
        var url = $"{ApiBase}/stays/search";

        using var doc = await PostJsonAsync(client, url, body, ct).ConfigureAwait(false);
        var retrievedAt = DateTimeOffset.UtcNow;
        var nights = Math.Max(1, q.CheckOut.DayNumber - q.CheckIn.DayNumber);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("results", out var staysResults) ||
            staysResults.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<StayOffer>();
        }

        var results = new List<StayOffer>();
        foreach (var result in staysResults.EnumerateArray())
        {
            var mapped = DuffelNormalizer.NormalizeStayOffer(result, q, nights, retrievedAt);
            if (mapped is not null)
                results.Add(mapped);
        }
        return results;
    }

    // ── Request building ──────────────────────────────────────────────────────────────────────────────

    private static string BuildOfferRequestBody(FareQuery q)
    {
        // slices: one per leg — two for a round trip. passengers: one {type:adult} per adult.
        var slices = new StringBuilder();
        slices.Append('[');
        AppendSlice(slices, q.From, q.To, q.DepartDate);
        if (q.ReturnDate is { } ret)
        {
            slices.Append(',');
            AppendSlice(slices, q.To, q.From, ret);
        }
        slices.Append(']');

        var passengers = string.Join(",", Enumerable.Repeat("{\"type\":\"adult\"}", Math.Max(1, q.Adults)));
        var cabin = CabinFor(q.Cabin);

        return "{\"data\":{\"cabin_class\":\"" + cabin + "\",\"passengers\":[" + passengers +
               "],\"slices\":" + slices + "}}";
    }

    private static void AppendSlice(StringBuilder sb, string origin, string destination, DateOnly date) =>
        sb.Append(CultureInfo.InvariantCulture,
            $"{{\"origin\":\"{origin}\",\"destination\":\"{destination}\",\"departure_date\":\"{date:yyyy-MM-dd}\"}}");

    private static string BuildStaySearchBody(StayQuery q, int radiusKm)
    {
        var lat = q.Lat.ToString("0.######", CultureInfo.InvariantCulture);
        var lng = q.Lng.ToString("0.######", CultureInfo.InvariantCulture);
        var guests = string.Join(",", Enumerable.Repeat("{\"type\":\"adult\"}", Math.Max(1, q.Adults)));
        var rooms = Math.Max(1, q.Rooms).ToString(CultureInfo.InvariantCulture);
        var checkIn = q.CheckIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var checkOut = q.CheckOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var radius = radiusKm.ToString(CultureInfo.InvariantCulture);

        return "{\"data\":{\"rooms\":" + rooms +
               ",\"guests\":[" + guests +
               "],\"check_in_date\":\"" + checkIn +
               "\",\"check_out_date\":\"" + checkOut +
               "\",\"location\":{\"radius\":" + radius +
               ",\"geographic_coordinates\":{\"latitude\":" + lat +
               ",\"longitude\":" + lng + "}}}}";
    }

    private static string CabinFor(CabinClass cabin) => cabin switch
    {
        CabinClass.PremiumEconomy => "premium_economy",
        CabinClass.Business => "business",
        CabinClass.First => "first",
        _ => "economy",
    };

    // ── HTTP ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticated <c>POST</c> with the mandatory Duffel headers (<c>Duffel-Version: v2</c>,
    /// <c>Accept</c>/<c>Content-Type: application/json</c>). The broker stamps the bearer; the plugin never
    /// touches the token (travel-fares-plugin.md §3). A <c>401</c> surfaces a clear config error; a
    /// <c>429</c> is surfaced so the host's per-plugin circuit breaker/rate budget can open and the
    /// aggregator fans out across the other providers (§11/§13).
    /// </summary>
    private async Task<JsonDocument> PostJsonAsync(HttpClient client, string url, string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Duffel-Version", DuffelVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        await _host.Auth.ApplyAsync(request, scopes: null, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(
                "Duffel authentication failed (401). The broker's access token was rejected; verify the " +
                "configured environment (test vs live) and the vaulted API key (travel-fares-plugin.md §3).");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException(
                "Duffel rate limit hit (429); backing off to ratelimit-reset (travel-fares-plugin.md §11).",
                inner: null, statusCode: HttpStatusCode.TooManyRequests);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }
}
