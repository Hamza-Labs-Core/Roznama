using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Aggregation;

/// <summary>
/// Routes a <c>geo.route</c> query across every registered <see cref="IRouteProvider"/>
/// (ARCHITECTURE.md §15, PLUGIN-HOST.md §9). Honors per-provider <see cref="RouteCoverage"/> to skip
/// providers that can't serve the requested <see cref="TravelMode"/> (e.g. OSRM has no transit). The
/// host-computed <see cref="RouteResult.LeaveByUtc"/>/<see cref="RouteResult.Feasible"/> are left at their
/// provider defaults here — the aggregator only picks the winner and stamps <see cref="RouteResult.Source"/>.
/// </summary>
public interface IRouteAggregator
{
    Task<AggregationResult<RouteResult>> RouteAsync(
        GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when,
        AggregationOptions? options, CancellationToken ct);
}

/// <summary>Aggregates <c>geo.geocode</c> across every <see cref="IGeocoder"/> (forward geocode only — the path the planner needs).</summary>
public interface IGeocodeAggregator
{
    Task<AggregationResult<Place>> GeocodeAsync(
        string query, GeoBias? bias, AggregationOptions? options, CancellationToken ct);
}

/// <summary>Aggregates <c>geo.places</c> autocomplete across every <see cref="IPlaceSearch"/>.</summary>
public interface IPlaceSearchAggregator
{
    Task<AggregationResult<IReadOnlyList<PlaceSuggestion>>> SuggestAsync(
        string query, GeoBias? bias, AggregationOptions? options, CancellationToken ct);
}

/// <summary>
/// Aggregates <c>flight.price</c> across every <see cref="IFlightPricing"/>: honors <see cref="FareCoverage"/>
/// (market/currency/one-way), dedupes on <c>Carrier+FlightNo+DepartUtc+ArriveUtc</c>, keeps the cheapest,
/// and tags the winning <see cref="FareOffer.Source"/> (ARCHITECTURE.md §15, SDK-CONTRACT.md §5.2).
/// </summary>
public interface IFlightPricingAggregator
{
    Task<AggregationResult<IReadOnlyList<FareOffer>>> SearchAsync(
        FareQuery query, AggregationOptions? options, CancellationToken ct);
}

/// <summary>
/// Aggregates <c>stay.price</c> across every <see cref="IStayPricing"/>: honors <see cref="FareCoverage"/>,
/// dedupes on accommodation/geo, keeps the cheapest, and tags the winning <see cref="StayOffer.Source"/>.
/// </summary>
public interface IStayPricingAggregator
{
    Task<AggregationResult<IReadOnlyList<StayOffer>>> SearchAsync(
        StayQuery query, AggregationOptions? options, CancellationToken ct);
}
