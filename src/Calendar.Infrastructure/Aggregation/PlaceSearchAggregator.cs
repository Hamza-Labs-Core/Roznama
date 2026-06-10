using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// Aggregates <c>geo.places</c> autocomplete across registered <see cref="IPlaceSearch"/>s
/// (ARCHITECTURE.md §15). Fan-out concatenates each provider's ranked candidates, dedupes near-identical
/// suggestions (same label + coordinate), and preserves provider order; failover returns the first
/// provider's non-empty list.
/// </summary>
public sealed class PlaceSearchAggregator : IPlaceSearchAggregator
{
    private readonly AggregationEngine _engine;

    public PlaceSearchAggregator(IPluginRegistry registry, IAggregationResultCache cache, ILogger<PlaceSearchAggregator> logger)
        => _engine = new AggregationEngine(registry, cache, logger);

    public Task<AggregationResult<IReadOnlyList<PlaceSuggestion>>> SuggestAsync(
        string query, GeoBias? bias, AggregationOptions? options, CancellationToken ct)
    {
        var key = $"geo.places|{query.Trim().ToLowerInvariant()}|{bias?.Lang}";

        return _engine.RunAsync<IPlaceSearch, IReadOnlyList<PlaceSuggestion>>(
            Capability.GeoPlaces,
            key,
            options,
            isEligible: _ => true,
            invoke: async (p, c) => await p.SuggestAsync(query, bias, c).ConfigureAwait(false),
            isNonEmpty: list => list is { Count: > 0 },
            merge: results => new SourcedResult<IReadOnlyList<PlaceSuggestion>>(
                AggregationEngine.JoinSources(results), Merge(results)),
            stamp: (list, _) => list, // each PlaceSuggestion already carries its own Source.
            ct);
    }

    private static IReadOnlyList<PlaceSuggestion> Merge(IReadOnlyList<SourcedResult<IReadOnlyList<PlaceSuggestion>>> results)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<PlaceSuggestion>();
        foreach (var r in results)
            foreach (var s in r.Value)
            {
                var dedupeKey = $"{s.Label.Trim().ToLowerInvariant()}|{s.Lat:F4},{s.Lng:F4}";
                if (seen.Add(dedupeKey))
                    merged.Add(s);
            }
        return merged;
    }
}
