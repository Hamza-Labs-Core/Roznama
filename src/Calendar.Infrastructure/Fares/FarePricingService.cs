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

    /// <inheritdoc />
    public async Task<FareOverlayResult> BuildOverlayAsync(OverlayRequest request, CancellationToken ct)
    {
        var currency = Coalesce(request.Currency, DefaultCurrency);
        if (request.To < request.From)
            return FareOverlayResult.Empty(request.Kind, currency);

        return request.Kind == FareOverlayKind.Flight
            ? await BuildFlightOverlayAsync(request, currency, ct).ConfigureAwait(false)
            : await BuildStayOverlayAsync(request, currency, ct).ConfigureAwait(false);
    }

    private async Task<FareOverlayResult> BuildFlightOverlayAsync(OverlayRequest request, string currency, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Origin) || string.IsNullOrWhiteSpace(request.Dest))
            return FareOverlayResult.Empty(request.Kind, currency);

        var market = Coalesce(request.Market, DefaultMarket);
        var cells = new Dictionary<DateOnly, OverlayCell>();
        string? winningSource = null;

        // One cached fan-out per candidate departure date over the window. The aggregator's short-TTL cache
        // collapses repeat range requests, so the grid does not pay one live call per cell on every refresh
        // (travel-fares-plugin.md §10.2). Whatever offers come back are bucketed to the cheapest per date.
        foreach (var date in EachDay(request.From, request.To))
        {
            ct.ThrowIfCancellationRequested();
            var query = new FareQuery(
                From: request.Origin!.Trim().ToUpperInvariant(),
                To: request.Dest!.Trim().ToUpperInvariant(),
                DepartDate: date,
                ReturnDate: null,
                Adults: Math.Max(1, request.Pax),
                Cabin: CabinClass.Economy,
                Currency: currency,
                Market: market);

            var result = await _flights.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
            if (!result.HasValue || result.Value is not { Count: > 0 } offers)
                continue;

            winningSource ??= result.WinningSource;
            foreach (var offer in offers)
            {
                if (offer.Date < request.From || offer.Date > request.To)
                    continue;
                Bucket(cells, offer.Date, offer.Price, offer.Source, result.Stale || offer.Stale);
            }
        }

        return new FareOverlayResult(request.Kind, currency, winningSource, cells);
    }

    private async Task<FareOverlayResult> BuildStayOverlayAsync(OverlayRequest request, string currency, CancellationToken ct)
    {
        if (request.Lat is not { } lat || request.Lng is not { } lng)
            return FareOverlayResult.Empty(request.Kind, currency);

        var market = Coalesce(request.Market, DefaultMarket);
        var nights = Math.Max(1, request.Nights);
        var cells = new Dictionary<DateOnly, OverlayCell>();
        string? winningSource = null;

        // Nightly-rate overlay: one cached fan-out per candidate check-in (check-out = check-in + nights).
        foreach (var checkIn in EachDay(request.From, request.To))
        {
            ct.ThrowIfCancellationRequested();
            var query = new StayQuery(
                Lat: lat, Lng: lng,
                RadiusKm: Math.Max(1, request.RadiusKm),
                CheckIn: checkIn,
                CheckOut: checkIn.AddDays(nights),
                Adults: Math.Max(1, request.Pax),
                Rooms: 1,
                Currency: currency,
                Market: market);

            var result = await _stays.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
            if (!result.HasValue || result.Value is not { Count: > 0 } offers)
                continue;

            winningSource ??= result.WinningSource;
            var cheapest = offers.MinBy(o => o.PricePerNight)!;
            Bucket(cells, checkIn, cheapest.PricePerNight, cheapest.Source, result.Stale || cheapest.Stale);
        }

        return new FareOverlayResult(request.Kind, currency, winningSource, cells);
    }

    private static void Bucket(IDictionary<DateOnly, OverlayCell> cells, DateOnly date, decimal price, string? source, bool stale)
    {
        if (!cells.TryGetValue(date, out var existing) || price < existing.Price)
            cells[date] = new OverlayCell(price, source, stale);
    }

    private static IEnumerable<DateOnly> EachDay(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            yield return d;
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
