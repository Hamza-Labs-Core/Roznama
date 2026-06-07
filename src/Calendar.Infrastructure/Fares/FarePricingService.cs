using Calendar.Application.Aggregation;
using Calendar.Application.Fares;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Fares;

/// <summary>
/// Default <see cref="IFarePricingService"/> (travel-fares-plugin.md §9, ARCHITECTURE.md §14/§15). It maps the
/// API request onto the SDK query records and dispatches through the interchangeable-pricing aggregators in
/// <see cref="AggregationOptions.FanOut"/> mode so identical offers are deduped, the cheapest kept, and the
/// winning <c>Source</c> tagged — failing over and degrading to stale last-known history without ever throwing
/// to the planner. When no provider is registered/configured the aggregator simply produces no value, which we
/// surface as <see cref="FarePricingResult{T}.Empty"/> (no overlay, no crash).
/// </summary>
public sealed class FarePricingService : IFarePricingService
{
    private const string DefaultCurrency = "USD";
    private const string DefaultMarket = "US";

    private readonly IFlightPricingAggregator _flights;
    private readonly IStayPricingAggregator _stays;
    private readonly ILogger<FarePricingService> _logger;

    public FarePricingService(
        IFlightPricingAggregator flights,
        IStayPricingAggregator stays,
        ILogger<FarePricingService> logger)
    {
        _flights = flights;
        _stays = stays;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FarePricingResult<FareOffer>> SearchFlightsAsync(FlightSearchRequest request, CancellationToken ct)
    {
        var query = new FareQuery(
            From: request.From.Trim().ToUpperInvariant(),
            To: request.To.Trim().ToUpperInvariant(),
            DepartDate: request.Depart,
            ReturnDate: request.Return,
            Adults: Math.Max(1, request.Adults),
            Cabin: request.Cabin,
            Currency: Coalesce(request.Currency, DefaultCurrency),
            Market: Coalesce(request.Market, DefaultMarket));

        var result = await _flights.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
        return ToResult(result);
    }

    /// <inheritdoc />
    public async Task<FarePricingResult<StayOffer>> SearchStaysAsync(StaySearchRequest request, CancellationToken ct)
    {
        var query = new StayQuery(
            Lat: request.Lat,
            Lng: request.Lng,
            RadiusKm: Math.Max(1, request.RadiusKm),
            CheckIn: request.CheckIn,
            CheckOut: request.CheckOut,
            Adults: Math.Max(1, request.Adults),
            Rooms: Math.Max(1, request.Rooms),
            Currency: Coalesce(request.Currency, DefaultCurrency),
            Market: Coalesce(request.Market, DefaultMarket));

        var result = await _stays.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
        return ToResult(result);
    }

    private FarePricingResult<T> ToResult<T>(AggregationResult<IReadOnlyList<T>> result)
    {
        if (!result.HasValue || result.Value is not { Count: > 0 } offers)
        {
            // No provider registered/configured, or every eligible provider was empty/down with no history.
            _logger.LogDebug("Fare pricing produced no offers (source={Source}, stale={Stale}).",
                result.WinningSource, result.Stale);
            return FarePricingResult<T>.Empty;
        }

        return new FarePricingResult<T>(offers, result.WinningSource, result.Stale);
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
