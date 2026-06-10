using Calendar.Application.Aggregation;
using Calendar.Application.Fares;
using Calendar.Infrastructure.Aggregation;
using Calendar.Infrastructure.Fares;
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

        // The host pricing facade fronts both aggregators for the /api/fares endpoints (travel-fares-plugin.md §9).
        services.AddSingleton<IFarePricingService, FarePricingService>();

        return services;
    }
}
