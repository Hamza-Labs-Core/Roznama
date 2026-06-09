using Calendar.Application.Calendars;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Toolbar event search (ROADMAP Phase 6 polish): case-insensitive substring match over title + location on
/// visible calendars, newest-first so upcoming plans surface before old history. Pure local query — search
/// never touches a provider.
/// </summary>
public sealed class EventSearchService : IEventSearchService
{
    private const int MaxLimit = 100;

    private readonly CalendarDbContext _db;

    public EventSearchService(CalendarDbContext db) => _db = db;

    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResultDto>();

        // Escape LIKE wildcards so a literal "%"/"_" in the query doesn't widen the match.
        var escaped = query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = $"%{escaped}%";
        var take = Math.Clamp(limit, 1, MaxLimit);

        return await (
            from e in _db.Events
            join c in _db.Calendars on e.CalendarId equals c.Id
            where c.IsVisible &&
                  (EF.Functions.Like(e.Title!, pattern, "\\") ||
                   EF.Functions.Like(e.Location!, pattern, "\\"))
            orderby e.StartUtc descending
            select new SearchResultDto(
                e.Id, c.Id, c.Name, e.Title ?? string.Empty,
                e.StartUtc, e.EndUtc, e.AllDay, e.Location))
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
