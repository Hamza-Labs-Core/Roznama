using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Microsoft.Tests;

/// <summary>
/// Pure normalization of canned Graph JSON → domain (microsoft-graph-plugin.md §8). No network: every assertion
/// runs against a literal calendar/event JSON element.
/// </summary>
public class MicrosoftNormalizerTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── Calendars ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Calendar_maps_id_name_hexcolor_and_editable_access()
    {
        var cal = MicrosoftNormalizer.NormalizeCalendar(Parse("""
            {"id":"AAA=","name":"Calendar","color":"auto","hexColor":"#1a73e8","canEdit":true,
             "isDefaultCalendar":true}
            """));

        Assert.Equal("AAA=", cal.RemoteId);
        Assert.Equal("Calendar", cal.Name);
        Assert.Equal("#1a73e8", cal.Color); // precise hexColor preferred over the coarse enum
        Assert.False(cal.IsReadOnly);
    }

    [Fact]
    public void Calendar_without_canEdit_false_is_read_only()
    {
        var cal = MicrosoftNormalizer.NormalizeCalendar(Parse("""
            {"id":"SHARED=","name":"Team (shared)","color":"lightBlue","canEdit":false}
            """));

        Assert.True(cal.IsReadOnly);
        Assert.Equal("Team (shared)", cal.Name);
        Assert.Null(cal.Color); // coarse enum has no hex → null (host applies a default)
    }

    // ── Timed events ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timed_event_in_utc_resolves_and_keeps_uid_and_etag()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {
              "id":"EV1","iCalUId":"040000008200E0@example","subject":"Standup","type":"singleInstance",
              "@odata.etag":"W/\"abc\"","isAllDay":false,"isCancelled":false,"showAs":"busy",
              "location":{"displayName":"Room 5"},
              "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
              "end":{"dateTime":"2026-06-10T09:30:00.0000000","timeZone":"UTC"}
            }
            """));

        Assert.Equal("EV1", ev.RemoteId);
        Assert.Equal("040000008200E0@example", ev.Uid); // iCalUId feeds cross-source dedup
        Assert.Equal("Standup", ev.Title);
        Assert.False(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero), ev.EndUtc);
        Assert.Equal("Room 5", ev.Location);
        Assert.Equal("W/\"abc\"", ev.ChangeTag);
        Assert.Equal(EventStatus.Confirmed, ev.Status);
        Assert.Null(ev.RecurrenceId);
    }

    [Fact]
    public void Timed_event_in_named_zone_converts_to_utc()
    {
        // Without Prefer:outlook.timezone=UTC, Graph returns the mailbox zone. We honor the label defensively.
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {
              "id":"EV2","iCalUId":"u2","subject":"Lunch","type":"singleInstance",
              "start":{"dateTime":"2026-06-10T12:00:00.0000000","timeZone":"Eastern Standard Time"},
              "end":{"dateTime":"2026-06-10T13:00:00.0000000","timeZone":"Eastern Standard Time"}
            }
            """));

        // 12:00 EDT (June, DST) == 16:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 16, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal("Eastern Standard Time", ev.TimeZoneId);
    }

    // ── All-day ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void All_day_event_makes_end_inclusive()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"D1","iCalUId":"d1","subject":"Holiday","type":"singleInstance","isAllDay":true,
             "start":{"dateTime":"2026-12-25T00:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-12-26T00:00:00.0000000","timeZone":"UTC"}}
            """));

        Assert.True(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        // Graph end is exclusive (26th) → inclusive end == start for a single-day all-day event.
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero), ev.EndUtc);
    }

    [Fact]
    public void Multi_day_all_day_subtracts_one_from_exclusive_end()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"D2","iCalUId":"d2","subject":"Trip","type":"singleInstance","isAllDay":true,
             "start":{"dateTime":"2026-07-01T00:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-07-04T00:00:00.0000000","timeZone":"UTC"}}
            """));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), ev.EndUtc); // 07-04 excl → 07-03 incl
    }

    // ── Recurrence ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Series_master_translates_weekly_pattern_to_rrule()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"M1","iCalUId":"m1","subject":"Weekly sync","type":"seriesMaster",
             "start":{"dateTime":"2026-06-01T10:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-06-01T11:00:00.0000000","timeZone":"UTC"},
             "recurrence":{
               "pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday","wednesday"]},
               "range":{"type":"numbered","numberOfOccurrences":10,"startDate":"2026-06-01"}
             }}
            """));

        Assert.NotNull(ev.Rrule);
        Assert.Contains("FREQ=WEEKLY", ev.Rrule);
        Assert.Contains("BYDAY=MO,WE", ev.Rrule);
        Assert.Contains("COUNT=10", ev.Rrule);
        Assert.Null(ev.RecurrenceId); // a master, not an expanded instance
    }

    [Fact]
    public void Exception_instance_is_keyed_by_its_start()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"EX1","iCalUId":"ex1","subject":"Weekly sync (moved)","type":"exception",
             "seriesMasterId":"M1",
             "start":{"dateTime":"2026-06-08T14:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-06-08T15:00:00.0000000","timeZone":"UTC"}}
            """));

        Assert.Null(ev.Rrule); // expanded instances carry no RRULE under Strategy A
        Assert.NotNull(ev.RecurrenceId);
        Assert.Equal("2026-06-08T14:00:00.0000000Z", ev.RecurrenceId);
    }

    [Fact]
    public void Absolute_monthly_pattern_with_enddate_translates()
    {
        var rrule = MicrosoftNormalizer.SerializeRecurrence(Parse("""
            {"recurrence":{
               "pattern":{"type":"absoluteMonthly","interval":2,"dayOfMonth":15},
               "range":{"type":"endDate","startDate":"2026-01-15","endDate":"2026-12-31"}
            }}
            """));

        Assert.NotNull(rrule);
        Assert.Contains("FREQ=MONTHLY", rrule);
        Assert.Contains("BYMONTHDAY=15", rrule);
        Assert.Contains("INTERVAL=2", rrule);
        Assert.Contains("UNTIL=20261231", rrule);
    }

    [Fact]
    public void Relative_monthly_pattern_uses_bysetpos()
    {
        var rrule = MicrosoftNormalizer.SerializeRecurrence(Parse("""
            {"recurrence":{
               "pattern":{"type":"relativeMonthly","interval":1,"daysOfWeek":["friday"],"index":"last"},
               "range":{"type":"noEnd","startDate":"2026-01-01"}
            }}
            """));

        Assert.NotNull(rrule);
        Assert.Contains("FREQ=MONTHLY", rrule);
        Assert.Contains("BYDAY=FR", rrule);
        Assert.Contains("BYSETPOS=-1", rrule);
        Assert.DoesNotContain("COUNT", rrule);
        Assert.DoesNotContain("UNTIL", rrule);
    }

    // ── Cancelled / coordinates ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancelled_event_maps_to_cancelled_status()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"C1","iCalUId":"c1","subject":"Gone","type":"singleInstance","isCancelled":true,
             "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-06-10T10:00:00.0000000","timeZone":"UTC"}}
            """));

        Assert.Equal(EventStatus.Cancelled, ev.Status);
    }

    [Fact]
    public void Location_coordinates_populate_geo()
    {
        var ev = MicrosoftNormalizer.NormalizeEvent(Parse("""
            {"id":"G1","iCalUId":"g1","subject":"Offsite","type":"singleInstance",
             "location":{"displayName":"HQ","coordinates":{"latitude":47.6062,"longitude":-122.3321}},
             "categories":["Work","Travel"],
             "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
             "end":{"dateTime":"2026-06-10T10:00:00.0000000","timeZone":"UTC"}}
            """));

        Assert.NotNull(ev.Geo);
        Assert.Equal(47.6062, ev.Geo!.Value.Lat, 4);
        Assert.Equal(-122.3321, ev.Geo!.Value.Lng, 4);
        Assert.Equal(new[] { "Work", "Travel" }, ev.Categories);
    }
}
