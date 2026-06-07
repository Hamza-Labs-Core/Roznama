using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Fares;

/// <summary>
/// The host-side fare/stay pricing facade (docs/deep-dives/travel-fares-plugin.md §9, ARCHITECTURE.md §14/§15).
/// It builds a <see cref="FareQuery"/>/<see cref="StayQuery"/> from the API request and runs it THROUGH the
/// interchangeable-pricing aggregators (<c>IFlightPricingAggregator</c>/<c>IStayPricingAggregator</c>) so the
/// answer is deduped, cheapest-picked, source-tagged, and degrades to stale last-known when every provider is
/// down/empty. It never names a provider — the registry decides who prices flights/stays.
///
/// <para>When no pricing provider is registered or configured the result is simply <b>empty</b> (no overlay,
/// no crash) — the planner treats "no provider" identically to "no offers" (travel-fares-plugin.md §13).</para>
/// </summary>
public interface IFarePricingService
{
    /// <summary>Price a flight route+dates through the flight aggregator (fan-out: dedupe + cheapest + source).</summary>
    Task<FarePricingResult<FareOffer>> SearchFlightsAsync(FlightSearchRequest request, CancellationToken ct);

    /// <summary>Price stays around a coordinate+dates through the stay aggregator (fan-out: dedupe + cheapest + source).</summary>
    Task<FarePricingResult<StayOffer>> SearchStaysAsync(StaySearchRequest request, CancellationToken ct);

    /// <summary>
    /// Build the multi-month planner's per-date price overlay (travel-fares-plugin.md §10.2, ARCHITECTURE §14):
    /// a <c>date → cheapest price</c> map across the visible window for one route (flight) or place (stay). The
    /// query runs THROUGH the interchangeable-pricing aggregator (fan-out → dedupe → cheapest → source-tag) and the
    /// returned offers are bucketed by departure date / check-in to the cheapest per cell. The fan-out is cached so
    /// the grid's range request does not issue one live call per cell. When no provider is registered/configured the
    /// map is simply <b>empty</b> (no overlay, no crash — §13).
    /// </summary>
    Task<FareOverlayResult> BuildOverlayAsync(OverlayRequest request, CancellationToken ct);
}

/// <summary>
/// A multi-month overlay request as accepted by <c>GET /api/fares/overlay</c>. <see cref="Kind"/> selects the
/// flight cheapest-date or stay nightly-rate overlay; <see cref="From"/>..<see cref="To"/> is the visible window.
/// Flights need <see cref="Origin"/>/<see cref="Dest"/> IATA codes; stays need <see cref="Lat"/>/<see cref="Lng"/>.
/// </summary>
public sealed record OverlayRequest(
    FareOverlayKind Kind,
    DateOnly From, DateOnly To,
    // Flight
    string? Origin = null, string? Dest = null,
    // Stay
    double? Lat = null, double? Lng = null, int RadiusKm = 5, int Nights = 1,
    int Pax = 1, string? Currency = null, string? Market = null);

/// <summary>Which price overlay to paint on the planner.</summary>
public enum FareOverlayKind
{
    /// <summary>Cheapest flight fare per departure date.</summary>
    Flight,
    /// <summary>Nightly stay rate per check-in date.</summary>
    Stay,
}

/// <summary>
/// The overlay payload: a per-date cheapest-price map plus the winning source (if any). An empty
/// <see cref="Cells"/> means "no provider registered/configured or none had an answer" — the planner paints
/// nothing (travel-fares-plugin.md §13).
/// </summary>
public sealed record FareOverlayResult(
    FareOverlayKind Kind,
    string Currency,
    string? Source,
    IReadOnlyDictionary<DateOnly, OverlayCell> Cells)
{
    /// <summary>An empty overlay (no provider/no offers).</summary>
    public static FareOverlayResult Empty(FareOverlayKind kind, string currency) =>
        new(kind, currency, Source: null, new Dictionary<DateOnly, OverlayCell>());
}

/// <summary>One painted cell: the cheapest price for that date, its source tag, and whether it is a stale last-known.</summary>
public sealed record OverlayCell(decimal Price, string? Source, bool Stale);

/// <summary>A flight pricing request as accepted by <c>GET /api/fares/flights</c>.</summary>
public sealed record FlightSearchRequest(
    string From, string To,
    DateOnly Depart, DateOnly? Return,
    int Adults = 1,
    CabinClass Cabin = CabinClass.Economy,
    string? Currency = null, string? Market = null);

/// <summary>A stay pricing request as accepted by <c>GET /api/fares/stays</c>.</summary>
public sealed record StaySearchRequest(
    double Lat, double Lng,
    DateOnly CheckIn, DateOnly CheckOut,
    int Adults = 1, int Rooms = 1, int RadiusKm = 5,
    string? Currency = null, string? Market = null);

/// <summary>
/// The aggregated pricing outcome: the deduped/cheapest-sorted offers plus the per-query observability the
/// aggregator records (winning source(s), whether the value is a stale last-known fallback). Empty offers with
/// no winning source mean "no provider registered/configured or none had an answer" — handled gracefully.
/// </summary>
public sealed record FarePricingResult<T>(
    IReadOnlyList<T> Offers,
    string? Source,
    bool Stale)
{
    /// <summary>An empty result (no provider/no offers): no source, not stale.</summary>
    public static FarePricingResult<T> Empty { get; } =
        new(Array.Empty<T>(), Source: null, Stale: false);
}
