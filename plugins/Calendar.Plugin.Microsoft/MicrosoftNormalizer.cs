using System.Globalization;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Microsoft;

/// <summary>
/// Pure Microsoft Graph JSON → domain normalization (microsoft-graph-plugin.md §8). No network, no host — so
/// it is exhaustively unit-testable from canned <c>/me/calendars</c> and <c>calendarView/delta</c> payloads.
///
/// Strategy A (microsoft-graph-plugin.md §7): <c>calendarView/delta</c> emits recurrence already expanded as
/// <c>occurrence</c>/<c>exception</c> rows, so the plugin stores concrete instances keyed by <c>id</c> and
/// relates them to their master via <c>seriesMasterId</c> (mapped onto <see cref="RemoteEvent.RecurrenceId"/>
/// for an exception/occurrence). The master's <c>recurrence</c> pattern is preserved on a <c>seriesMaster</c>
/// row (which appears only on <c>/me/events</c>, not <c>calendarView</c>) so a future Strategy-B migration is
/// possible. We request <c>Prefer: outlook.timezone="UTC"</c>, so <c>start.dateTime</c>/<c>end.dateTime</c>
/// already arrive in UTC; we still honor a non-UTC <c>timeZone</c> label defensively via TimeZoneInfo.
/// </summary>
public static class MicrosoftNormalizer
{
    /// <summary>
    /// Map one <c>/me/calendars</c> item to a <see cref="RemoteCalendar"/> (microsoft-graph-plugin.md §4).
    /// Prefer the precise <c>hexColor</c>; fall back to the coarse <c>color</c> enum. <c>canEdit == false</c>
    /// (shared/reader calendars) maps to read-only.
    /// </summary>
    public static RemoteCalendar NormalizeCalendar(JsonElement item)
    {
        var id = GetString(item, "id") ?? throw new FormatException("Graph calendar is missing 'id'.");
        var name = GetString(item, "name") ?? id;

        // hexColor is the precise value ("#1a73e8"); color is a coarse enum ("auto", "lightBlue", ...).
        var hex = GetString(item, "hexColor");
        var color = !string.IsNullOrWhiteSpace(hex) && hex != "" ? hex : NormalizeColorEnum(GetString(item, "color"));

        // canEdit absent → assume editable (owned). Only an explicit false marks the calendar read-only.
        var readOnly = item.TryGetProperty("canEdit", out var canEdit)
                       && canEdit.ValueKind == JsonValueKind.False;

        return new RemoteCalendar(id, name, color, readOnly);
    }

    /// <summary>
    /// Map one <c>calendarView/delta</c> (or <c>/me/events</c>) item to a <see cref="RemoteEvent"/>
    /// (microsoft-graph-plugin.md §8). An <c>isCancelled == true</c> event is surfaced as
    /// <see cref="EventStatus.Cancelled"/> (a tombstone the caller routes to <c>Deletes</c>); a delta
    /// <c>@removed</c> tombstone is handled by the plugin before calling this (it carries no normalizable body).
    /// </summary>
    public static RemoteEvent NormalizeEvent(JsonElement ev)
    {
        var id = GetString(ev, "id") ?? throw new FormatException("Graph event is missing 'id'.");

        // iCalUId is stable across calendars/mailboxes for the same meeting → feeds dedup. Note it DIFFERS per
        // occurrence in a series, so it is not a series key (microsoft-graph-plugin.md §7). Fall back to id.
        var uid = GetString(ev, "iCalUId") ?? id;

        var isAllDay = ev.TryGetProperty("isAllDay", out var allDayEl)
                       && allDayEl.ValueKind == JsonValueKind.True;
        var isCancelled = ev.TryGetProperty("isCancelled", out var cancelledEl)
                          && cancelledEl.ValueKind == JsonValueKind.True;

        DateTimeOffset startUtc = default, endUtc = default;
        string? tzId = null;

        if (ev.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Object)
        {
            if (isAllDay)
            {
                // All-day: start/end are midnight in the same zone; Graph end is EXCLUSIVE (the day after the
                // last day). Store an inclusive end date (microsoft-graph-plugin.md §8, §14).
                var startDate = DateOnly.FromDateTime(ParseGraphDateTime(start).Local);
                DateOnly? exclusiveEnd = ev.TryGetProperty("end", out var endAllDay)
                                         && endAllDay.ValueKind == JsonValueKind.Object
                    ? DateOnly.FromDateTime(ParseGraphDateTime(endAllDay).Local)
                    : null;
                var inclusiveEnd = exclusiveEnd is { } x && x > startDate ? x.AddDays(-1) : startDate;
                startUtc = ToUtcMidnight(startDate);
                endUtc = ToUtcMidnight(inclusiveEnd);
            }
            else
            {
                var parsedStart = ParseGraphDateTime(start);
                startUtc = parsedStart.Utc;
                tzId = parsedStart.TimeZoneLabel;
                endUtc = ev.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Object
                    ? ParseGraphDateTime(end).Utc
                    : startUtc;
            }
        }

        // type ∈ {singleInstance, occurrence, exception, seriesMaster}. For the master, persist the RRULE; for
        // an expanded occurrence/exception, key it on the original start instant so the host can splice it.
        var type = GetString(ev, "type");
        var rrule = string.Equals(type, "seriesMaster", StringComparison.OrdinalIgnoreCase)
            ? SerializeRecurrence(ev)
            : null;

        string? recurrenceId = null;
        if (string.Equals(type, "occurrence", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "exception", StringComparison.OrdinalIgnoreCase))
        {
            // The instance's own start is its occurrence key (the original-start instant within the series).
            recurrenceId = startUtc == default
                ? null
                : startUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }

        string? location = null;
        GeoPoint? geo = null;
        if (ev.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            location = GetString(loc, "displayName");
            if (loc.TryGetProperty("coordinates", out var coords) && coords.ValueKind == JsonValueKind.Object
                && TryGetDouble(coords, "latitude", out var lat) && TryGetDouble(coords, "longitude", out var lng))
            {
                geo = new GeoPoint(lat, lng);
            }
        }

        var categories = ReadCategories(ev);

        // @odata.etag is the per-event change tag; changeKey is the equivalent on the typed projection.
        var etag = GetString(ev, "@odata.etag") ?? GetString(ev, "changeKey");

        return new RemoteEvent(
            RemoteId: id,
            Uid: uid,
            Title: GetString(ev, "subject") ?? string.Empty,
            StartUtc: startUtc,
            EndUtc: endUtc,
            TimeZoneId: tzId,
            AllDay: isAllDay,
            Rrule: rrule,
            RecurrenceId: recurrenceId,
            Location: string.IsNullOrWhiteSpace(location) ? null : location,
            Geo: geo,
            Categories: categories,
            ChangeTag: etag,
            Status: isCancelled ? EventStatus.Cancelled : MapShowAs(GetString(ev, "showAs")));
    }

    /// <summary>
    /// Translate a Graph <c>PatternedRecurrence</c> to an iCalendar <c>RRULE</c> (microsoft-graph-plugin.md §7).
    /// Covers the common daily/weekly/absolute&amp;relative-monthly/yearly patterns plus <c>numbered</c>/
    /// <c>endDate</c> ranges; unknown shapes return null so the host falls back to the expanded instances.
    /// </summary>
    public static string? SerializeRecurrence(JsonElement ev)
    {
        if (!ev.TryGetProperty("recurrence", out var rec) || rec.ValueKind != JsonValueKind.Object)
            return null;
        if (!rec.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object)
            return null;

        var type = GetString(pattern, "type")?.ToLowerInvariant();
        var interval = TryGetInt(pattern, "interval", out var iv) ? iv : 1;

        var parts = new List<string>();
        switch (type)
        {
            case "daily":
                parts.Add("FREQ=DAILY");
                break;
            case "weekly":
                parts.Add("FREQ=WEEKLY");
                AppendByDay(pattern, parts);
                break;
            case "absolutemonthly":
                parts.Add("FREQ=MONTHLY");
                if (TryGetInt(pattern, "dayOfMonth", out var dom))
                    parts.Add($"BYMONTHDAY={dom}");
                break;
            case "relativemonthly":
                parts.Add("FREQ=MONTHLY");
                AppendByDay(pattern, parts);
                AppendBySetPos(pattern, parts);
                break;
            case "absoluteyearly":
                parts.Add("FREQ=YEARLY");
                if (TryGetInt(pattern, "month", out var ym))
                    parts.Add($"BYMONTH={ym}");
                if (TryGetInt(pattern, "dayOfMonth", out var ydom))
                    parts.Add($"BYMONTHDAY={ydom}");
                break;
            case "relativeyearly":
                parts.Add("FREQ=YEARLY");
                if (TryGetInt(pattern, "month", out var rym))
                    parts.Add($"BYMONTH={rym}");
                AppendByDay(pattern, parts);
                AppendBySetPos(pattern, parts);
                break;
            default:
                return null;
        }

        if (interval > 1)
            parts.Add($"INTERVAL={interval}");

        // Range: numbered (COUNT) or endDate (UNTIL); noEnd → infinite.
        if (rec.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Object)
        {
            var rangeType = GetString(range, "type")?.ToLowerInvariant();
            if (rangeType == "numbered" && TryGetInt(range, "numberOfOccurrences", out var count) && count > 0)
                parts.Add($"COUNT={count}");
            else if (rangeType == "enddate" && GetString(range, "endDate") is { Length: > 0 } endDate
                     && DateOnly.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
                parts.Add($"UNTIL={until:yyyyMMdd}");
        }

        return "RRULE:" + string.Join(";", parts);
    }

    private static void AppendByDay(JsonElement pattern, List<string> parts)
    {
        if (!pattern.TryGetProperty("daysOfWeek", out var days) || days.ValueKind != JsonValueKind.Array)
            return;
        var byDay = new List<string>();
        foreach (var d in days.EnumerateArray())
        {
            if (d.ValueKind == JsonValueKind.String && MapWeekday(d.GetString()) is { } code)
                byDay.Add(code);
        }
        if (byDay.Count > 0)
            parts.Add($"BYDAY={string.Join(",", byDay)}");
    }

    private static void AppendBySetPos(JsonElement pattern, List<string> parts)
    {
        // index ∈ {first, second, third, fourth, last} → BYSETPOS 1..4 / -1.
        var setPos = GetString(pattern, "index")?.ToLowerInvariant() switch
        {
            "first" => "1",
            "second" => "2",
            "third" => "3",
            "fourth" => "4",
            "last" => "-1",
            _ => null,
        };
        if (setPos is not null)
            parts.Add($"BYSETPOS={setPos}");
    }

    private static string? MapWeekday(string? day) => day?.ToLowerInvariant() switch
    {
        "sunday" => "SU",
        "monday" => "MO",
        "tuesday" => "TU",
        "wednesday" => "WE",
        "thursday" => "TH",
        "friday" => "FR",
        "saturday" => "SA",
        _ => null,
    };

    /// <summary>
    /// Graph's coarse <c>color</c> enum has no hex; only <c>hexColor</c> does. When only the enum is present we
    /// can't fabricate a hex, so return null (the host applies a default) unless it already looks like a hex.
    /// </summary>
    private static string? NormalizeColorEnum(string? color)
    {
        if (string.IsNullOrWhiteSpace(color) || color == "auto")
            return null;
        return color.StartsWith('#') ? color : null;
    }

    private static EventStatus MapShowAs(string? showAs) => showAs?.Trim().ToLowerInvariant() switch
    {
        "tentative" => EventStatus.Tentative,
        _ => EventStatus.Confirmed,
    };

    private static IReadOnlyList<string> ReadCategories(JsonElement ev)
    {
        if (!ev.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var c in cats.EnumerateArray())
            if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list.Count == 0 ? Array.Empty<string>() : list;
    }

    /// <summary>
    /// Parse a Graph <c>dateTimeTimeZone</c> (<c>{ dateTime, timeZone }</c>). With
    /// <c>Prefer: outlook.timezone="UTC"</c> the <c>dateTime</c> is UTC and <c>timeZone</c> == "UTC". We honor a
    /// non-UTC label defensively (Windows or IANA name) via TimeZoneInfo so the UTC instant is always correct.
    /// </summary>
    private static (DateTimeOffset Utc, DateTime Local, string? TimeZoneLabel) ParseGraphDateTime(JsonElement dtz)
    {
        var raw = GetString(dtz, "dateTime")
                  ?? throw new FormatException("Graph dateTimeTimeZone is missing 'dateTime'.");
        var label = GetString(dtz, "timeZone");

        // Graph's dateTime is a local wall-clock without an offset (e.g. "2026-06-10T09:00:00.0000000"). Parse
        // it unanchored (Unspecified) — the timeZone label, not an embedded offset, decides the instant.
        var wall = DateTime.SpecifyKind(
            DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None),
            DateTimeKind.Unspecified);

        if (string.IsNullOrWhiteSpace(label) || IsUtcLabel(label))
        {
            var asUtc = DateTime.SpecifyKind(wall, DateTimeKind.Utc);
            return (new DateTimeOffset(asUtc), asUtc, label is null || IsUtcLabel(label) ? null : label);
        }
        if (TryFindTimeZone(label!, out var tz))
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(wall, tz);
            return (new DateTimeOffset(utc), wall, label);
        }

        // Unknown zone → fall back to treating the wall-clock as UTC (best effort), keep the label.
        return (new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc)), wall, label);
    }

    private static bool IsUtcLabel(string label) =>
        label.Equals("UTC", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Coordinated Universal Time", StringComparison.OrdinalIgnoreCase);

    private static bool TryFindTimeZone(string label, out TimeZoneInfo tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(label);
            return true;
        }
        catch (Exception)
        {
            // .NET 9 resolves both Windows and IANA ids on all platforms; an unknown id lands here.
            tz = TimeZoneInfo.Utc;
            return false;
        }
    }

    private static DateTimeOffset ToUtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt(JsonElement parent, string property, out int value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement parent, string property, out double value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetDouble(out value))
            return true;
        value = 0;
        return false;
    }
}
