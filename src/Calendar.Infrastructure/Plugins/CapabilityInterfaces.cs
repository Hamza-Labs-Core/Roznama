using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Maps a <see cref="Capability"/> to the SDK interface a plugin must implement to satisfy it
/// (SDK-CONTRACT.md §9). Used by capability binding (PLUGIN-HOST.md §3.5): a plugin declaring a capability
/// it doesn't actually implement is faulted.
/// </summary>
public static class CapabilityInterfaces
{
    private static readonly IReadOnlyDictionary<Capability, Type> Map = new Dictionary<Capability, Type>
    {
        [Capability.CalendarRead]    = typeof(ICalendarSource),
        [Capability.CalendarWrite]   = typeof(ICalendarWriter),
        [Capability.ItineraryImport] = typeof(IItinerarySource),
        [Capability.FlightPrice]     = typeof(IFlightPricing),
        [Capability.StayPrice]       = typeof(IStayPricing),
        [Capability.GeoTiles]        = typeof(ITileProvider),
        [Capability.GeoGeocode]      = typeof(IGeocoder),
        [Capability.GeoRoute]        = typeof(IRouteProvider),
        [Capability.GeoPlaces]       = typeof(IPlaceSearch),
        [Capability.Notify]          = typeof(INotifier),
    };

    /// <summary>The interface type backing a capability.</summary>
    public static Type For(Capability capability) => Map[capability];

    /// <summary>True if <paramref name="plugin"/> implements the interface for <paramref name="capability"/>.</summary>
    public static bool IsImplementedBy(Capability capability, IPlugin plugin) =>
        Map[capability].IsInstanceOfType(plugin);
}
