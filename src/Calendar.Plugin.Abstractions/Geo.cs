namespace Calendar.Plugin.Abstractions;

/// <summary>A bare coordinate. The canonical lightweight geo value type used across routing, geocoding, and events.</summary>
public readonly record struct GeoPoint(double Lat, double Lng);

/// <summary>An axis-aligned bounding box (viewport) for biasing geocoding/place search. Order matches GeoJSON-ish (min/max lng/lat).</summary>
public readonly record struct BBox(double MinLng, double MinLat, double MaxLng, double MaxLat);

/// <summary>Map-center / viewport bias + language for geocoding and autocomplete (geo-geocoding-places-plugin.md §2).</summary>
public sealed record GeoBias(
    double? Lat, double? Lng,                   // map-center proximity bias
    BBox? ViewBox,                              // viewport restriction (Nominatim viewbox, Photon bbox)
    string? Lang);                              // preferred result language (accept-language / lang)

/// <summary>
/// A resolved place. Maps to the domain PLACE (ARCHITECTURE.md §9). <see cref="Source"/> records which
/// provider answered (attribution / aggregator tagging).
/// </summary>
public sealed record Place(
    double Lat, double Lng,
    string Label,                               // human-readable display name
    string? Address,                            // parsed/full address when available
    string Source);                             // provider id: "nominatim" | "photon" | "google" | …

/// <summary>One ranked autocomplete candidate from <c>geo.places</c>.</summary>
public sealed record PlaceSuggestion(
    string Label,
    double Lat, double Lng,
    string? Address,
    string Source);

/// <summary>capability <c>geo.geocode</c>. Text → best place (forward) and coords → place (reverse, for map click-to-place).</summary>
public interface IGeocoder : IPlugin
{
    /// <summary>Forward geocode: one query → the single best <see cref="Place"/>, or null (caller keeps raw text, retries later).</summary>
    Task<Place?> GeocodeAsync(string query, GeoBias? bias, CancellationToken ct);

    /// <summary>Reverse geocode a coordinate to a place (powers map click-to-place). Null if nothing resolves.</summary>
    Task<Place?> ReverseGeocodeAsync(GeoPoint point, CancellationToken ct);
}

/// <summary>capability <c>geo.places</c>. Search-as-you-type autocomplete: partial query + bias → ranked candidates.</summary>
public interface IPlaceSearch : IPlugin
{
    Task<IReadOnlyList<PlaceSuggestion>> SuggestAsync(string query, GeoBias? bias, CancellationToken ct);
}

/// <summary>Travel mode the planner asks for (geo-routing-plugin.md §2).</summary>
public enum TravelMode { Drive, Transit, Walk, Bike }

/// <summary>capability <c>geo.route</c>. Origin/destination/mode/time → duration + geometry (interchangeable; aggregated).</summary>
public interface IRouteProvider : IPlugin
{
    /// <summary>Modes this provider can actually answer — drives policy (who gets Transit; who is traffic-aware).</summary>
    RouteCoverage Coverage { get; }

    /// <summary>
    /// Route between two points. <paramref name="when"/> is the NEXT EVENT'S START; the plugin interprets it
    /// per mode (Drive → departureTime; Transit → arrivalTime). The plugin returns DurationSec (+ optional
    /// Geometry) only; it MUST leave <see cref="RouteResult.LeaveByUtc"/> null and <see cref="RouteResult.Feasible"/>
    /// true — those are host-computed against the inter-event gap. Throw
    /// <see cref="System.NotSupportedException"/> for a mode the provider can't serve so the aggregator fails over.
    /// </summary>
    Task<RouteResult> RouteAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct);
}

/// <summary>Which travel modes (and traffic-awareness) a routing provider supports. Read by the aggregator's policy.</summary>
public sealed record RouteCoverage(
    bool Drive, bool Transit, bool Walk, bool Bike,
    bool TrafficAware);                         // true → preferred for time-sensitive drive timing

/// <summary>
/// A routing answer. <see cref="DurationSec"/> and <see cref="Geometry"/> come from the provider;
/// <see cref="LeaveByUtc"/> and <see cref="Feasible"/> are computed by the HOST (they depend on the gap a
/// provider can't see) and left at their defaults by plugins; <see cref="Source"/> is stamped by the aggregator.
/// </summary>
public sealed record RouteResult(
    int DurationSec,                            // provider's predicted travel time (traffic-aware where supported)
    string? Geometry,                           // encoded polyline, precision 5 (normalize Valhalla's precision-6 in the plugin); null if not requested
    DateTimeOffset? LeaveByUtc,                 // HOST-COMPUTED: arriveBy − DurationSec − buffer; plugins leave null
    bool Feasible,                              // HOST-COMPUTED: gap ≥ DurationSec; plugins default true
    string Source);                             // winning plugin id (e.g. "osrm","google") → RouteLeg.Source

/// <summary>
/// capability <c>geo.tiles</c>. Supplies the Map view's basemap as a MapLibre style. Usually pure declarative
/// config; the assembly interface exists for providers that must compute a style or mint per-session tokens
/// (e.g. Google) (geo-tiles-plugin.md §2).
/// </summary>
public interface ITileProvider : IPlugin
{
    /// <summary>Resolve the basemap to render (called when the Map view mounts and on style switch).</summary>
    Task<StyleDescriptor> GetStyleAsync(CancellationToken ct);
}

/// <summary>A resolved MapLibre basemap. <see cref="StyleUrl"/> and <see cref="StyleJson"/> are mutually exclusive.</summary>
public sealed record StyleDescriptor(
    string? StyleUrl,                           // e.g. https://…/style.json?key=…  (most providers)
    string? StyleJson,                          // inline MapLibre style (self-hosted / PMTiles assembled at runtime)
    string Attribution,                         // required credit line shown in the attribution control — non-optional
    string TileKind,                            // "vector" | "raster" — informational; the style is authoritative
    bool SupportsOffline);                      // may the PWA cache visited tiles? (license-dependent)
