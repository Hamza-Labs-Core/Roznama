using System.Globalization;
using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// Aggregates <c>geo.route</c> across registered <see cref="IRouteProvider"/>s (ARCHITECTURE.md §15,
/// PLUGIN-HOST.md §9). Coverage filtering skips a provider whose <see cref="RouteCoverage"/> can't serve
/// the requested mode (e.g. OSRM has no transit → a transit query fails over to a transit-capable
/// provider). Fan-out picks the fastest route. The host-computed <see cref="RouteResult.LeaveByUtc"/> and
/// <see cref="RouteResult.Feasible"/> are deliberately left at their provider defaults — those depend on
/// the inter-event gap the aggregator can't see and are filled in by the domain after a winner is chosen.
/// </summary>
public sealed class RouteAggregator : IRouteAggregator
{
    private readonly AggregationEngine _engine;

    public RouteAggregator(IPluginRegistry registry, IAggregationResultCache cache, ILogger<RouteAggregator> logger)
        => _engine = new AggregationEngine(registry, cache, logger);

    public Task<AggregationResult<RouteResult>> RouteAsync(
        GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when,
        AggregationOptions? options, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"geo.route|{from.Lat:F5},{from.Lng:F5}->{to.Lat:F5},{to.Lng:F5}|{mode}|{when:O}");

        return _engine.RunAsync<IRouteProvider, RouteResult>(
            Capability.GeoRoute,
            key,
            options,
            isEligible: p => Supports(p.Coverage, mode),
            invoke: (p, c) => InvokeAsync(p, from, to, mode, when, c),
            isNonEmpty: r => r is { DurationSec: > 0 },
            merge: results => results.MinBy(r => r.Value.DurationSec), // fastest route; its provider is the winner.
            stamp: (r, source) => r with { Source = source },
            ct);
    }

    private static async Task<RouteResult?> InvokeAsync(
        IRouteProvider provider, GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct)
    {
        try
        {
            return await provider.RouteAsync(from, to, mode, when, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            // A provider that advertised the mode but can't serve this specific query → treat as empty so
            // the aggregator fails over rather than aborting (SDK-CONTRACT.md §6.3).
            return null;
        }
    }

    private static bool Supports(RouteCoverage coverage, TravelMode mode) => mode switch
    {
        TravelMode.Drive => coverage.Drive,
        TravelMode.Transit => coverage.Transit,
        TravelMode.Walk => coverage.Walk,
        TravelMode.Bike => coverage.Bike,
        _ => false
    };
}
