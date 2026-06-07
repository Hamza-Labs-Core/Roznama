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
}

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
