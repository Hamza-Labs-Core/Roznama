using System.Globalization;
using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// Aggregates <c>stay.price</c> across registered <see cref="IStayPricing"/>s (ARCHITECTURE.md §15,
/// SDK-CONTRACT.md §5.2). Coverage filtering skips providers that don't serve the query's market/currency.
/// Fan-out dedupes on accommodation/geo (place label + coordinate + stay dates), keeps the cheapest total,
/// and sorts cheapest first; each kept offer keeps the winning provider's <see cref="StayOffer.Source"/>.
/// </summary>
public sealed class StayPricingAggregator : IStayPricingAggregator
{
    private readonly AggregationEngine _engine;

    public StayPricingAggregator(IPluginRegistry registry, IAggregationResultCache cache, ILogger<StayPricingAggregator> logger)
        => _engine = new AggregationEngine(registry, cache, logger);

    public Task<AggregationResult<IReadOnlyList<StayOffer>>> SearchAsync(
        StayQuery query, AggregationOptions? options, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"stay.price|{query.Lat:F4},{query.Lng:F4}|{query.RadiusKm}|{query.CheckIn:yyyyMMdd}->{query.CheckOut:yyyyMMdd}|{query.Adults}|{query.Rooms}|{query.Currency}|{query.Market}");

        return _engine.RunAsync<IStayPricing, IReadOnlyList<StayOffer>>(
            Capability.StayPrice,
            key,
            options,
            isEligible: p => Serves(p.Coverage, query),
            invoke: async (p, c) => await p.SearchAsync(query, c).ConfigureAwait(false),
            isNonEmpty: list => list is { Count: > 0 },
            merge: results => new SourcedResult<IReadOnlyList<StayOffer>>(
                AggregationEngine.JoinSources(results), Merge(results)),
            stamp: (list, _) => list,
            ct);
    }

    private static bool Serves(FareCoverage coverage, StayQuery q)
    {
        if (coverage.Markets.Count > 0 && !coverage.Markets.Contains(q.Market))
            return false;
        if (coverage.Currencies.Count > 0 && !coverage.Currencies.Contains(q.Currency))
            return false;
        return true;
    }

    private static IReadOnlyList<StayOffer> Merge(IReadOnlyList<SourcedResult<IReadOnlyList<StayOffer>>> results)
    {
        var cheapest = new Dictionary<string, StayOffer>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
            foreach (var offer in r.Value)
            {
                var dedupeKey = string.Create(CultureInfo.InvariantCulture,
                    $"{offer.PlaceLabel.Trim().ToLowerInvariant()}|{offer.Lat:F4},{offer.Lng:F4}|{offer.CheckIn:yyyyMMdd}|{offer.CheckOut:yyyyMMdd}");
                if (!cheapest.TryGetValue(dedupeKey, out var existing) || offer.PriceTotal < existing.PriceTotal)
                    cheapest[dedupeKey] = offer;
            }
        return cheapest.Values.OrderBy(o => o.PriceTotal).ToArray();
    }
}
