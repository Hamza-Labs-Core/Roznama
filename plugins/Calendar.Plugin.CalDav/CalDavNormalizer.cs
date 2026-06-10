using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Calendar.Plugin.Abstractions;
using Ical.Net.CalendarComponents;
using Ical.Net.Serialization.DataTypes;

namespace Calendar.Plugin.CalDav;

/// <summary>
/// Pure iCalendar → domain normalization for CalDAV resources (caldav-plugin.md §8). No network, no host —
/// so it is exhaustively unit-testable from canned <c>calendar-data</c> bodies. A single CalDAV resource is
/// one iCalendar object holding a master <c>VEVENT</c> plus any <c>RECURRENCE-ID</c> override components; this
/// normalizer maps every component to a <see cref="RemoteEvent"/>. Recurrence is stored as the master RRULE
/// and expanded on demand by the host; overrides arrive as their own rows keyed by <c>(UID, RECURRENCE-ID)</c>.
/// </summary>
/// <remarks>
/// The mapping logic mirrors the ICS plugin's <c>IcsNormalizer</c> (the deep-dive says to reuse those patterns)
/// but this plugin is kept self-contained: addressing is by resource <c>href</c> and the change tag is the
/// resource <c>ETag</c> (caldav-plugin.md §8), not the ICS sequence/last-modified pair.
/// </remarks>
public static class CalDavNormalizer
{
    private static readonly RecurrencePatternSerializer RruleSerializer = new();

    /// <summary>
    /// Parse one CalDAV resource body into its normalized events. <paramref name="href"/> is the resource href
    /// (the domain <c>RemoteId</c> base), <paramref name="etag"/> the resource ETag (the per-event change tag).
    /// A body with a recurrence master + overrides yields one row per component.
    /// </summary>
    public static IReadOnlyList<RemoteEvent> Parse(string calendarData, string href, string? etag)
    {
        var calendar = Ical.Net.Calendar.Load(calendarData);
        var events = new List<RemoteEvent>(calendar.Events.Count);
        foreach (var ev in calendar.Events)
            events.Add(Normalize(ev, href, etag));
        return events;
    }

    private static RemoteEvent Normalize(CalendarEvent ev, string href, string? etag)
    {
        var uid = string.IsNullOrWhiteSpace(ev.Uid) ? SynthesizeUid(ev) : ev.Uid;

        DateTimeOffset startUtc, endUtc;
        bool allDay = ev.IsAllDay;
        string? tzId = null;

        if (allDay)
        {
            var start = DateOnly.FromDateTime(ev.Start.Value);
            // RFC 5545 all-day DTEND is exclusive; store an inclusive end date.
            var exclusiveEnd = ev.End is { } e ? DateOnly.FromDateTime(e.Value) : start.AddDays(1);
            var inclusiveEnd = exclusiveEnd > start ? exclusiveEnd.AddDays(-1) : start;
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

        // Address by href; an override within the same resource is keyed by its RECURRENCE-ID so master and
        // exception don't collide on a single href (caldav-plugin.md §8).
        var remoteId = recurrenceId is { } r
            ? $"{href}|{r.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)}"
            : href;

        var categories = new List<string>(ev.Categories ?? Enumerable.Empty<string>());

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
            ChangeTag: etag,
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

    private static string SynthesizeUid(CalendarEvent ev)
    {
        var seed = $"{ev.Summary}|{ev.Start?.AsUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return $"synthetic-{Convert.ToHexStringLower(hash)[..16]}";
    }
}
