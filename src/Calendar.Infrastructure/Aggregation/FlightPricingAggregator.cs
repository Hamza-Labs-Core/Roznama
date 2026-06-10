using System.Globalization;
using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// Aggregates <c>flight.price</c> across registered <see cref="IFlightPricing"/>s (ARCHITECTURE.md §15,
/// SDK-CONTRACT.md §5.2). Coverage filtering skips a provider that doesn't serve the query's market,
/// currency, or one-way/round-trip shape before any call. Fan-out dedupes on
/// <c>Carrier+FlightNo+DepartUtc+ArriveUtc</c>, keeping the cheapest offer per itinerary, sorted cheapest
/// first; each kept offer keeps the <see cref="FareOffer.Source"/> of the provider that won it.
/// </summary>
public sealed class FlightPricingAggregator : IFlightPricingAggregator
{
    private readonly AggregationEngine _engine;

    public FlightPricingAggregator(IPluginRegistry registry, IAggregationResultCache cache, ILogger<FlightPricingAggregator> logger)
        => _engine = new AggregationEngine(registry, cache, logger);

    public Task<AggregationResult<IReadOnlyList<FareOffer>>> SearchAsync(
        FareQuery query, AggregationOptions? options, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"flight.price|{query.From}->{query.To}|{query.DepartDate:yyyyMMdd}|{query.ReturnDate:yyyyMMdd}|{query.Adults}|{query.Cabin}|{query.Currency}|{query.Market}");

        return _engine.RunAsync<IFlightPricing, IReadOnlyList<FareOffer>>(
            Capability.FlightPrice,
            key,
            options,
            isEligible: p => Serves(p.Coverage, query),
            invoke: async (p, c) => await p.SearchAsync(query, c).ConfigureAwait(false),
            isNonEmpty: list => list is { Count: > 0 },
            merge: results => new SourcedResult<IReadOnlyList<FareOffer>>(
                AggregationEngine.JoinSources(results), Merge(results)),
            stamp: (list, _) => list, // each FareOffer carries its own provider Source.
            ct);
    }

    private static bool Serves(FareCoverage coverage, FareQuery q)
    {
        if (coverage.Markets.Count > 0 && !coverage.Markets.Contains(q.Market))
            return false;
        if (coverage.Currencies.Count > 0 && !coverage.Currencies.Contains(q.Currency))
            return false;
        if (q.ReturnDate is null && !coverage.OneWay)
            return false;
        return true;
    }

    private static IReadOnlyList<FareOffer> Merge(IReadOnlyList<SourcedResult<IReadOnlyList<FareOffer>>> results)
    {
        // Dedupe on the SDK-specified key, keeping the cheapest fare per itinerary; cheapest overall first.
        var cheapest = new Dictionary<string, FareOffer>(StringComparer.Ordinal);
        foreach (var r in results)
            foreach (var offer in r.Value)
            {
                var dedupeKey = $"{offer.Carrier}|{offer.FlightNo}|{offer.DepartUtc:O}|{offer.ArriveUtc:O}";
                if (!cheapest.TryGetValue(dedupeKey, out var existing) || offer.Price < existing.Price)
                    cheapest[dedupeKey] = offer;
            }
        return cheapest.Values.OrderBy(o => o.Price).ToArray();
    }
}
