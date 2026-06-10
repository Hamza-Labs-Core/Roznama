namespace Calendar.Application.Calendars;

/// <summary>
/// A projected event that carries resolved coordinates — one map pin (ARCHITECTURE.md §7). Only events with
/// a geocoded <c>Place</c> appear; the calendar <see cref="Color"/> rides along so pins inherit calendar color.
/// </summary>
public sealed record MapEvent(
    Guid Id,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double Lat,
    double Lng,
    string PlaceLabel,
    string? Color);

/// <summary>A resolved location pin (DATA-SCHEMA §2.5 PLACE), surfaced for the places list.</summary>
public sealed record PlaceDto(
    Guid Id,
    string Label,
    double Lat,
    double Lng,
    string? Address,
    string? Source);

/// <summary>
/// Read models for the Map view (ARCHITECTURE.md §7): events that have a resolved place (pins) and the full
/// place catalog. Pure projections over the local store — no provider calls.
/// </summary>
public interface IMapViewService
{
    /// <summary>Events overlapping [from, to) that have a geocoded <c>Place</c>, as map pins with coordinates.</summary>
    Task<IReadOnlyList<MapEvent>> GetMapEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Every resolved place pin.</summary>
    Task<IReadOnlyList<PlaceDto>> ListPlacesAsync(CancellationToken ct);
}

/// <summary>
/// One leg of a trip route (ROADMAP Phase 5 "trips drawn as routes"). <see cref="Geometry"/> is the
/// provider's encoded polyline (precision 5) when a <c>geo.route</c> plugin covered the leg; null means the
/// client draws a straight line between the endpoints (flights, ferries, or no provider installed).
/// </summary>
public sealed record TripLeg(
    Guid FromEventId,
    Guid ToEventId,
    string FromLabel,
    string ToLabel,
    double FromLat,
    double FromLng,
    double ToLat,
    double ToLng,
    DateTimeOffset DepartUtc,
    DateTimeOffset ArriveUtc,
    string? Geometry,
    string? Source);

/// <summary>A trip: consecutive placed Travel-category events stitched into an ordered route.</summary>
public sealed record TripRoute(Guid TripId, string Name, IReadOnlyList<TripLeg> Legs);

/// <summary>
/// Builds trip routes for the Map view: placed Travel-category events in the window, ordered by time,
/// grouped into trips, with each consecutive pair becoming a <see cref="TripLeg"/>. Road geometry comes
/// from the <c>geo.route</c> aggregator through the same <c>RouteLeg</c> cache the commute pipeline uses.
/// </summary>
public interface ITripRouteService
{
    Task<IReadOnlyList<TripRoute>> GetTripRoutesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
