using System.Net;
using Calendar.Application.Calendars;
using Calendar.Application.Fares;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Fares;
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

        // Live-refresh pub/sub behind GET /sync/stream (UI.md §9): engines publish, SSE connections listen.
        services.AddSingleton<IChangeFeed, ChangeFeed>();

        // One pooled HttpClient (decompression on) reused by the sync service for plugin fetches.
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        }));

        services.AddScoped<DeviceProvider>();
        services.AddScoped<DedupGrouper>();
        services.AddScoped<ISecretVault, SecretVault>();
        services.AddScoped<PluginHostServicesFactory>();
        services.AddScoped<ICalendarSyncService, CalendarSyncService>();
        services.AddScoped<ISyncScheduler, SyncScheduler>();
        services.AddScoped<IWriteService, WriteService>();
        services.AddScoped<IEventProjectionService, EventProjectionService>();
        services.AddScoped<IGeocodeService, GeocodeService>();
        services.AddScoped<IRouteService, RouteService>();
        services.AddScoped<IMapViewService, MapViewService>();
        services.AddScoped<ITripRouteService, TripRouteService>();
        services.AddScoped<IMapStyleService, MapStyleService>();
        services.AddScoped<IAccountService, AccountService>();
        // Generic connect (any plugin/auth scheme). The pending-OAuth map spans begin/callback requests.
        services.AddSingleton<PendingOAuthConnects>();
        services.AddScoped<IAccountConnectService, AccountConnectService>();
        services.AddScoped<ICalendarCatalog, CalendarCatalog>();
        services.AddScoped<IShareService, ShareService>();

        // Phase 6 polish: reminders, one-shot ICS import, toolbar search.
        services.AddScoped<IReminderService, ReminderService>();
        services.AddScoped<IImportService, ImportService>();
        services.AddScoped<IEventSearchService, EventSearchService>();

        // Optional E2E-encrypted cloud sync (ADR-0003): device engine + the self-hosted relay store.
        services.AddSingleton<Calendar.Application.Cloud.IRelayClient>(
            sp => new Cloud.HttpRelayClient(sp.GetRequiredService<HttpClient>()));
        services.AddScoped<Calendar.Application.Cloud.IRelayStore, Cloud.RelayStore>();
        services.AddScoped<Calendar.Application.Cloud.ICloudSyncService, Cloud.CloudSyncService>();

        // ── Fare watches + price history + notify-on-drop (ARCHITECTURE §14, travel-fares-plugin.md §10) ──
        // The in-app notifier is the persisted source of truth and also the read side; it is registered as both
        // an INotifier (the poll fans out to every notifier) and the INotificationReader the API reads from.
        services.AddScoped<InAppNotifier>();
        services.AddScoped<IFareNotifier>(sp => sp.GetRequiredService<InAppNotifier>());
        services.AddScoped<INotificationReader>(sp => sp.GetRequiredService<InAppNotifier>());
        services.AddScoped<IFareWatchService, FareWatchService>();

        return services;
    }
}
