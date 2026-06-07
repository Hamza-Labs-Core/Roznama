using Calendar.Application.Aggregation;
using Calendar.Infrastructure.Aggregation;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Hosting;

/// <summary>
/// Registers the multi-provider fallback aggregator over the plugin registry (ARCHITECTURE.md §15,
/// PLUGIN-HOST.md §9): one aggregator per interchangeable capability plus the shared TTL result cache.
/// </summary>
public static class AggregationServiceCollectionExtensions
{
    public static IServiceCollection AddAggregation(this IServiceCollection services)
    {
        // One shared, process-wide result cache (fresh-TTL + last-known stale fallback).
        services.AddSingleton<IAggregationResultCache, InMemoryAggregationResultCache>();

        services.AddSingleton<IRouteAggregator, RouteAggregator>();
        services.AddSingleton<IGeocodeAggregator, GeocodeAggregator>();
        services.AddSingleton<IPlaceSearchAggregator, PlaceSearchAggregator>();
        services.AddSingleton<IFlightPricingAggregator, FlightPricingAggregator>();
        services.AddSingleton<IStayPricingAggregator, StayPricingAggregator>();

        return services;
    }
}
