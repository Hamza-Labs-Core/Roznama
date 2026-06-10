using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Calendar.Plugin.Abstractions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization.DataTypes;

namespace Calendar.Plugin.Ics;

/// <summary>One parsed feed: its calendar metadata plus the normalized events (ics-plugin.md §6, §9).</summary>
public sealed record IcsParseResult(string? CalendarName, string? Color, IReadOnlyList<RemoteEvent> Events);

/// <summary>
/// Pure iCalendar → domain normalization using Ical.Net (ics-plugin.md §6, §9). No network, no host — so it
/// is exhaustively unit-testable from canned <c>.ics</c> text. Recurrence is stored as the master RRULE and
/// expanded on demand by the host; overrides arrive as their own rows keyed by <c>(UID, RECURRENCE-ID)</c>.
/// </summary>
public static class IcsNormalizer
{
    private static readonly RecurrencePatternSerializer RruleSerializer = new();

    public static IcsParseResult Parse(string icalendar, string? forceCategory = null)
    {
        var calendar = Ical.Net.Calendar.Load(icalendar);

        var name = GetProperty(calendar, "X-WR-CALNAME");
        var color = GetProperty(calendar, "X-APPLE-CALENDAR-COLOR");

        var events = new List<RemoteEvent>(calendar.Events.Count);
        foreach (var ev in calendar.Events)
            events.Add(Normalize(ev, forceCategory));

        return new IcsParseResult(name, color, events);
    }

    private static RemoteEvent Normalize(CalendarEvent ev, string? forceCategory)
    {
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? SynthesizeUid(ev) : ev.Uid;

        DateTimeOffset startUtc, endUtc;
        DateOnly? startDate = null;
        bool allDay = ev.IsAllDay;
        string? tzId = null;

        if (allDay)
        {
            var start = DateOnly.FromDateTime(ev.Start.Value);
            // RFC 5545 all-day DTEND is exclusive; store an inclusive end date.
            var exclusiveEnd = ev.End is { } e ? DateOnly.FromDateTime(e.Value) : start.AddDays(1);
            var inclusiveEnd = exclusiveEnd > start ? exclusiveEnd.AddDays(-1) : start;
            startDate = start;
            startUtc = ToUtcMidnight(start);
            endUtc = ToUtcMidnight(inclusiveEnd);
        }
        else
        {
            startUtc = new DateTimeOffset(DateTime.SpecifyKind(ev.Start.AsUtc, DateTimeKind.Utc));
            endUtc = ev.End is { } e
                ? new DateTimeOffset(DateTime.SpecifyKind(e.AsUtc, DateTimeKind.Utc))
                : startUtc;
            tzId = ev.Start.TzId;
        }

        var rrule = SerializeRrule(ev);

        DateTimeOffset? recurrenceId = ev.RecurrenceId is { } rid
            ? new DateTimeOffset(DateTime.SpecifyKind(rid.AsUtc, DateTimeKind.Utc))
            : null;

        var remoteId = recurrenceId is { } r
            ? $"{uid}|{r.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}"
            : uid;

        var categories = new List<string>(ev.Categories ?? Enumerable.Empty<string>());
        if (!string.IsNullOrWhiteSpace(forceCategory) &&
            !categories.Any(c => string.Equals(c, forceCategory, StringComparison.OrdinalIgnoreCase)))
        {
            categories.Add(forceCategory);
        }

        GeoPoint? geo = ev.GeographicLocation is { } g ? new GeoPoint(g.Latitude, g.Longitude) : null;

        return new RemoteEvent(
            RemoteId: remoteId,
            Uid: uid,
            Title: ev.Summary ?? string.Empty,
            StartUtc: startUtc,
            EndUtc: endUtc,
            TimeZoneId: tzId,
            AllDay: allDay,
            Rrule: rrule,
            RecurrenceId: recurrenceId?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            Location: string.IsNullOrWhiteSpace(ev.Location) ? null : ev.Location,
            Geo: geo,
            Categories: categories,
            ChangeTag: $"{ev.Sequence}:{ev.LastModified?.AsUtc.Ticks ?? 0}",
            Status: MapStatus(ev.Status));
    }

    private static string? SerializeRrule(CalendarEvent ev)
    {
        if (ev.RecurrenceRules is not { Count: > 0 } rules)
            return null;

        var parts = rules.Select(rp => RruleSerializer.SerializeToString(rp))
            .Where(s => !string.IsNullOrWhiteSpace(s));
        var joined = string.Join("\n", parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private static EventStatus MapStatus(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "CANCELLED" => EventStatus.Cancelled,
        "TENTATIVE" => EventStatus.Tentative,
        _ => EventStatus.Confirmed
    };

    private static DateTimeOffset ToUtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private static string? GetProperty(Ical.Net.Calendar calendar, string name) =>
        calendar.Properties.ContainsKey(name) ? calendar.Properties.Get<string>(name) : null;

    private static string SynthesizeUid(CalendarEvent ev)
    {
        var seed = $"{ev.Summary}|{ev.Start?.AsUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"synthetic-{Convert.ToHexStringLower(hash)[..16]}";
    }
}
