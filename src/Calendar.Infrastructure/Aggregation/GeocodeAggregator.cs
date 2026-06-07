using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// Aggregates forward <c>geo.geocode</c> across registered <see cref="IGeocoder"/>s (ARCHITECTURE.md §15).
/// A <c>null</c> forward-geocode result is the normal "no hit" case and is treated as empty so the
/// aggregator fails over to the next geocoder. Fan-out keeps the first resolving provider's place (geocode
/// has no cheaper/better axis to rank on) and tags its <see cref="Place.Source"/>.
/// </summary>
public sealed class GeocodeAggregator : IGeocodeAggregator
{
    private readonly AggregationEngine _engine;

    public GeocodeAggregator(IPluginRegistry registry, IAggregationResultCache cache, ILogger<GeocodeAggregator> logger)
        => _engine = new AggregationEngine(registry, cache, logger);

    public Task<AggregationResult<Place>> GeocodeAsync(
        string query, GeoBias? bias, AggregationOptions? options, CancellationToken ct)
    {
        var normalized = NormalizeQuery(query);
        var key = $"geo.geocode|{normalized}|{bias?.Lang}";

        return _engine.RunAsync<IGeocoder, Place>(
            Capability.GeoGeocode,
            key,
            options,
            isEligible: _ => true, // geocoders advertise no coverage descriptor; all are eligible.
            invoke: (p, c) => p.GeocodeAsync(query, bias, c),
            isNonEmpty: p => p is not null,
            merge: results => results[0], // first resolving provider wins.
            stamp: (p, source) => p with { Source = source },
            ct);
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
