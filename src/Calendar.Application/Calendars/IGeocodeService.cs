namespace Calendar.Application.Calendars;

/// <summary>
/// Resolves event <c>Location</c> free-text into a deduped <c>Place</c> pin, caching every lookup
/// (ARCHITECTURE.md §7, geo-geocoding-places-plugin.md §3). Hooked into the sync pipeline so newly-synced
/// events with a location are geocoded once and never again. Degrades gracefully: with no registered
/// <c>geo.geocode</c> provider it is a no-op (events keep their raw text, get no pin) and never throws.
/// </summary>
public interface IGeocodeService
{
    /// <summary>
    /// Geocode every event in the given calendar that has a <c>Location</c> but no <c>PlaceId</c> yet.
    /// Cache hits cost zero outbound calls; identical addresses collapse to one shared <c>Place</c>.
    /// </summary>
    /// <returns>How many events were newly assigned a resolved place.</returns>
    Task<int> GeocodePendingEventsAsync(Guid calendarId, CancellationToken ct);
}
