using Calendar.Application.Calendars;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Projects the local store into the Map view's read models (ARCHITECTURE.md §7): events with a resolved
/// <see cref="Domain.Entities.Place"/> become pins carrying coordinates + calendar color, and the full
/// place catalog backs the places list. Pure cache projection — switching to the map never re-queries a
/// provider.
/// </summary>
public sealed class MapViewService : IMapViewService
{
    private readonly CalendarDbContext _db;

    public MapViewService(CalendarDbContext db) => _db = db;

    public async Task<IReadOnlyList<MapEvent>> GetMapEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        // Only events that have a resolved place, on a visible calendar, overlapping the window. Recurring
        // masters carry one pin at the place (their occurrences share the same Location/Place).
        var rows = await (
            from e in _db.Events
            join c in _db.Calendars on e.CalendarId equals c.Id
            join p in _db.Places on e.PlaceId equals p.Id
            where c.IsVisible
                  && e.PlaceId != null
                  && (e.Rrule != null || (e.StartUtc < toUtc && e.EndUtc >= fromUtc))
            orderby e.StartUtc
            select new MapEvent(
                e.Id,
                e.Title ?? string.Empty,
                e.StartUtc,
                e.EndUtc,
                p.Lat,
                p.Lng,
                p.Label,
                c.Color))
            .ToListAsync(ct).ConfigureAwait(false);

        return rows;
    }

    public async Task<IReadOnlyList<PlaceDto>> ListPlacesAsync(CancellationToken ct) =>
        await _db.Places
            .OrderBy(p => p.Label)
            .Select(p => new PlaceDto(p.Id, p.Label, p.Lat, p.Lng, p.Address, p.Source))
            .ToListAsync(ct).ConfigureAwait(false);
}
