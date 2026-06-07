using Calendar.Application.Calendars;
using Calendar.Domain.Engines;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Projects stored events into a render-ready stream for a window (ARCHITECTURE §6): expand recurrence on
/// demand (Ical.Net), splice in overrides, apply the visibility pipeline (calendar + category + canonical),
/// and tag duplicate counts. Switching views never re-queries providers — this re-projects the cache.
/// </summary>
public sealed class EventProjectionService : IEventProjectionService
{
    private readonly CalendarDbContext _db;

    public EventProjectionService(CalendarDbContext db) => _db = db;

    public async Task<IReadOnlyList<ProjectedEvent>> GetEventsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var calendars = await _db.Calendars
            .Where(c => c.IsVisible)
            .ToDictionaryAsync(c => c.Id, c => c, ct).ConfigureAwait(false);
        if (calendars.Count == 0)
            return Array.Empty<ProjectedEvent>();

        var calendarIds = calendars.Keys.ToHashSet();

        // Generous SQL prefilter (>= on the end handles inclusive single-day all-day events); the precise
        // overlap — with all-day end treated as exclusive — is applied in memory below.
        var events = await _db.Events
            .Include(e => e.Categories)
            .Where(e => calendarIds.Contains(e.CalendarId) &&
                        (e.Rrule != null || (e.StartUtc < toUtc && e.EndUtc >= fromUtc)))
            .ToListAsync(ct).ConfigureAwait(false);

        // Duplicate-group canonical map + member counts.
        var groups = await _db.DuplicateGroups
            .ToDictionaryAsync(g => g.Id, g => g.CanonicalEventId, ct).ConfigureAwait(false);
        var groupCounts = events
            .Where(e => e.DuplicateGroupId is not null)
            .GroupBy(e => e.DuplicateGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        // Override original-starts per master, so an overridden occurrence isn't double-rendered.
        var overrideStartsByMaster = events
            .Where(e => e.MasterId is not null && e.RecurrenceId is not null)
            .GroupBy(e => e.MasterId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.RecurrenceId!.Value).ToHashSet());

        var result = new List<ProjectedEvent>();

        foreach (var ev in events)
        {
            if (!Visible(ev, groups))
                continue;

            if (ev.Rrule is { Length: > 0 })
            {
                var overrides = overrideStartsByMaster.GetValueOrDefault(ev.Id);
                foreach (var (start, end) in Expand(ev, fromUtc, toUtc))
                {
                    if (overrides is not null && overrides.Contains(start))
                        continue;
                    result.Add(Project(ev, calendars[ev.CalendarId], start, end, isRecurringInstance: true, groupCounts));
                }
            }
            else
            {
                // All-day inclusive end → exclusive for overlap, so a single-day event shows on its day.
                var effectiveEnd = ev.AllDay ? ev.EndUtc.AddDays(1) : ev.EndUtc;
                if (ev.StartUtc >= toUtc || effectiveEnd <= fromUtc)
                    continue;
                var isInstance = ev.MasterId is not null;
                result.Add(Project(ev, calendars[ev.CalendarId], ev.StartUtc, ev.EndUtc, isInstance, groupCounts));
            }
        }

        return result.OrderBy(e => e.StartUtc).ToList();
    }

    private bool Visible(Event ev, IReadOnlyDictionary<Guid, Guid?> groups)
    {
        var categoryVisibilities = ev.Categories.Select(c => c.IsVisible).ToArray();
        var inGroup = ev.DuplicateGroupId is not null;
        var isCanonical = inGroup && groups.TryGetValue(ev.DuplicateGroupId!.Value, out var canonical) && canonical == ev.Id;

        return VisibilityEvaluator.IsVisible(new VisibilityContext(
            CalendarVisible: true, // already filtered to visible calendars
            CategoryVisibilities: categoryVisibilities,
            InDuplicateGroup: inGroup,
            IsCanonical: isCanonical,
            WithinSelectedRange: true));
    }

    private static ProjectedEvent Project(
        Event ev, Domain.Entities.Calendar calendar, DateTimeOffset start, DateTimeOffset end,
        bool isRecurringInstance, IReadOnlyDictionary<Guid, int> groupCounts)
    {
        var duplicateCount = ev.DuplicateGroupId is { } gid && groupCounts.TryGetValue(gid, out var n) ? n - 1 : 0;
        return new ProjectedEvent(
            Id: ev.Id,
            CalendarId: ev.CalendarId,
            CalendarName: calendar.Name,
            Color: calendar.Color,
            Title: ev.Title ?? string.Empty,
            StartUtc: start,
            EndUtc: end,
            AllDay: ev.AllDay,
            Location: ev.Location,
            IsRecurringInstance: isRecurringInstance,
            MasterId: ev.MasterId ?? (isRecurringInstance ? ev.Id : null),
            DuplicateCount: Math.Max(0, duplicateCount),
            Categories: ev.Categories.Select(c => c.Name).ToList());
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Expand(
        Event master, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var duration = master.EndUtc - master.StartUtc;
        var calEvent = new CalendarEvent
        {
            Start = new CalDateTime(master.StartUtc.UtcDateTime, "UTC"),
            Duration = duration <= TimeSpan.Zero ? TimeSpan.Zero : duration,
        };
        foreach (var line in master.Rrule!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            calEvent.RecurrenceRules.Add(new RecurrencePattern(line));

        var occurrences = calEvent.GetOccurrences(fromUtc.UtcDateTime, toUtc.UtcDateTime);
        foreach (var occ in occurrences)
        {
            var start = new DateTimeOffset(DateTime.SpecifyKind(occ.Period.StartTime.AsUtc, DateTimeKind.Utc));
            yield return (start, start + duration);
        }
    }
}
