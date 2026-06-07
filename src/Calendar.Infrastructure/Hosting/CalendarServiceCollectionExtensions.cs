using System.Net;
using Calendar.Application.Calendars;
using Calendar.Infrastructure.Calendars;
using Calendar.Plugin.Abstractions;
using Calendar.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Hosting;

/// <summary>Registers the Phase 1 calendar pipeline: vault, sync, projection, accounts, catalog.</summary>
public static class CalendarServiceCollectionExtensions
{
    public static IServiceCollection AddCalendarServices(this IServiceCollection services)
    {
        // Shared, process-wide plugin snapshot cache (so ICS delta/diff survives across sync runs).
        services.AddSingleton<IPluginCache, InMemoryPluginCache>();

        // One pooled HttpClient (decompression on) reused by the sync service for plugin fetches.
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        }));

        services.AddScoped<DeviceProvider>();
        services.AddScoped<DedupGrouper>();
        services.AddScoped<ISecretVault, SecretVault>();
        services.AddScoped<ICalendarSyncService, CalendarSyncService>();
        services.AddScoped<IEventProjectionService, EventProjectionService>();
        services.AddScoped<IGeocodeService, GeocodeService>();
        services.AddScoped<IRouteService, RouteService>();
        services.AddScoped<IMapViewService, MapViewService>();
        services.AddScoped<IMapStyleService, MapStyleService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ICalendarCatalog, CalendarCatalog>();

        return services;
    }
}
