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
