using System.Globalization;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Google;

/// <summary>
/// Pure Google Calendar JSON → domain normalization (google-calendar-plugin.md §8, §9). No network, no host —
/// so it is exhaustively unit-testable from canned <c>CalendarList</c>/<c>Events.list</c> payloads. Recurrence
/// is kept as the master RRULE (<c>singleEvents=false</c>); moved/cancelled occurrences arrive as their own
/// items carrying <c>recurringEventId</c> + <c>originalStartTime</c> and are emitted as override rows.
/// </summary>
public static class GoogleNormalizer
{
    /// <summary>
    /// Map one <c>CalendarList.list</c> item to a <see cref="RemoteCalendar"/> (google §4). Holidays/Birthdays
    /// are surfaced as ordinary read-only calendars — never special-cased or dropped — so the domain dedup
    /// engine can collapse them across accounts.
    /// </summary>
    public static RemoteCalendar NormalizeCalendar(JsonElement item)
    {
        var id = GetString(item, "id") ?? throw new FormatException("CalendarList item is missing 'id'.");
        // summaryOverride (user-renamed) wins over the provider summary.
        var name = GetString(item, "summaryOverride") ?? GetString(item, "summary") ?? id;
        var color = GetString(item, "backgroundColor");
        var accessRole = GetString(item, "accessRole");
        var readOnly = accessRole is "reader" or "freeBusyReader";

        return new RemoteCalendar(id, name, color, readOnly);
    }

    /// <summary>
    /// Map one <c>Events.list</c> item to a <see cref="RemoteEvent"/> (google §9). A <c>status == "cancelled"</c>
    /// item is a tombstone (<see cref="EventStatus.Cancelled"/>) the host removes; the caller routes it to
    /// <c>Deletes</c>. Recurring-instance exceptions carry a non-null <see cref="RemoteEvent.RecurrenceId"/>.
    /// </summary>
    public static RemoteEvent NormalizeEvent(JsonElement ev)
    {
        var id = GetString(ev, "id") ?? throw new FormatException("Event is missing 'id'.");
        var status = MapStatus(GetString(ev, "status"));

        // iCalUID is stable across systems and feeds cross-source dedup; recurring instances share it but differ
        // by id. A cancelled tombstone may omit most fields, so fall back to the id when iCalUID is absent.
        var uid = GetString(ev, "iCalUID") ?? id;

        var allDay = ev.TryGetProperty("start", out var startEl)
                     && startEl.ValueKind == JsonValueKind.Object
                     && startEl.TryGetProperty("date", out _);

        DateTimeOffset startUtc = default, endUtc = default;
        string? tzId = null;

        if (ev.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            if (allDay)
            {
                var startDate = ParseDate(GetString(s, "date"));
                // Google all-day end.date is EXCLUSIVE; store an inclusive end date (google §9, §13).
                var exclusiveEnd = ev.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Object
                    ? ParseDate(GetString(e, "date"))
                    : (DateOnly?)null;
                var inclusiveEnd = exclusiveEnd is { } x && x > startDate
                    ? x.AddDays(-1)
                    : startDate;
                startUtc = ToUtcMidnight(startDate);
                endUtc = ToUtcMidnight(inclusiveEnd);
            }
            else
            {
                startUtc = ParseDateTime(s);
                tzId = GetString(s, "timeZone");
                endUtc = ev.TryGetProperty("end", out var e) && e.ValueKind == JsonValueKind.Object
                    ? ParseDateTime(e)
                    : startUtc;
            }
        }

        var rrule = SerializeRecurrence(ev);

        // recurringEventId + originalStartTime identify the master + the overridden occurrence. We key the
        // override on the original start instant so the host can splice it into the expanded series.
        string? recurrenceId = null;
        if (ev.TryGetProperty("originalStartTime", out var ost) && ost.ValueKind == JsonValueKind.Object)
        {
            if (ost.TryGetProperty("date", out _))
                recurrenceId = ToUtcMidnight(ParseDate(GetString(ost, "date")))
                    .UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
            else if (ost.TryGetProperty("dateTime", out _))
                recurrenceId = ParseDateTime(ost).UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        }

        var location = GetString(ev, "location");
        var etag = GetString(ev, "etag");

        return new RemoteEvent(
            RemoteId: id,
            Uid: uid,
            Title: GetString(ev, "summary") ?? string.Empty,
            StartUtc: startUtc,
            EndUtc: endUtc,
            TimeZoneId: tzId,
            AllDay: allDay,
            Rrule: rrule,
            RecurrenceId: recurrenceId,
            Location: string.IsNullOrWhiteSpace(location) ? null : location,
            Geo: null, // Google events carry no lat/lng; the host geocodes Location.
            Categories: Array.Empty<string>(),
            ChangeTag: etag,
            Status: status);
    }

    private static string? SerializeRecurrence(JsonElement ev)
    {
        if (!ev.TryGetProperty("recurrence", out var rec) || rec.ValueKind != JsonValueKind.Array)
            return null;

        var lines = new List<string>();
        foreach (var line in rec.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.String)
                continue;
            var s = line.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                lines.Add(s!);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static EventStatus MapStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "cancelled" => EventStatus.Cancelled,
        "tentative" => EventStatus.Tentative,
        _ => EventStatus.Confirmed
    };

    /// <summary>Parse a Google timed <c>EventDateTime</c> (<c>dateTime</c> = RFC3339 with offset) to UTC.</summary>
    private static DateTimeOffset ParseDateTime(JsonElement eventDateTime)
    {
        var dt = GetString(eventDateTime, "dateTime")
                 ?? throw new FormatException("EventDateTime has neither 'dateTime' nor 'date'.");
        var parsed = DateTimeOffset.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.ToUniversalTime();
    }

    private static DateOnly ParseDate(string? date) =>
        date is null
            ? throw new FormatException("All-day EventDateTime is missing 'date'.")
            : DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset ToUtcMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
