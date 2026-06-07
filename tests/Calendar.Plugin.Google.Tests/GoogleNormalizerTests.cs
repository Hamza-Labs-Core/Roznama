using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Google.Tests;

/// <summary>
/// Pure normalization of canned Google JSON → domain (google §8, §9). No network: every assertion runs against
/// a literal <c>CalendarList</c>/<c>Event</c> JSON element.
/// </summary>
public class GoogleNormalizerTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── CalendarList ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Calendar_maps_id_name_color_and_writable_access()
    {
        var cal = GoogleNormalizer.NormalizeCalendar(Parse("""
            {"id":"primary","summary":"My Calendar","backgroundColor":"#9fc6e7","accessRole":"owner","primary":true}
            """));

        Assert.Equal("primary", cal.RemoteId);
        Assert.Equal("My Calendar", cal.Name);
        Assert.Equal("#9fc6e7", cal.Color);
        Assert.False(cal.IsReadOnly);
    }

    [Fact]
    public void Calendar_summaryOverride_wins_over_summary()
    {
        var cal = GoogleNormalizer.NormalizeCalendar(Parse("""
            {"id":"c1","summary":"Original","summaryOverride":"Renamed","accessRole":"writer"}
            """));

        Assert.Equal("Renamed", cal.Name);
        Assert.False(cal.IsReadOnly);
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("freeBusyReader")]
    public void Holidays_and_birthdays_are_read_only_calendars(string accessRole)
    {
        var cal = GoogleNormalizer.NormalizeCalendar(Parse($$"""
            {"id":"en.usa#holiday@group.v.calendar.google.com","summary":"Holidays","accessRole":"{{accessRole}}"}
            """));

        Assert.True(cal.IsReadOnly);
        Assert.Equal("Holidays", cal.Name);
    }

    // ── Events ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timed_event_resolves_to_utc_and_keeps_timezone_and_uid()
    {
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {
              "id":"evt1","iCalUID":"abc@google.com","status":"confirmed","summary":"Standup",
              "location":"Room 5","etag":"\"etag-1\"",
              "start":{"dateTime":"2026-06-10T09:00:00-04:00","timeZone":"America/New_York"},
              "end":{"dateTime":"2026-06-10T09:30:00-04:00","timeZone":"America/New_York"}
            }
            """));

        Assert.Equal("evt1", ev.RemoteId);
        Assert.Equal("abc@google.com", ev.Uid); // iCalUID feeds cross-source dedup
        Assert.Equal("Standup", ev.Title);
        Assert.False(ev.AllDay);
        Assert.Equal("America/New_York", ev.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 13, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 13, 30, 0, TimeSpan.Zero), ev.EndUtc);
        Assert.Equal("Room 5", ev.Location);
        Assert.Equal("\"etag-1\"", ev.ChangeTag);
        Assert.Equal(EventStatus.Confirmed, ev.Status);
    }

    [Fact]
    public void All_day_event_uses_date_and_makes_end_inclusive()
    {
        // A one-day all-day event: Google end.date is exclusive (the next day).
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {"id":"d1","iCalUID":"d1@g","status":"confirmed","summary":"Holiday",
             "start":{"date":"2026-12-25"},"end":{"date":"2026-12-26"}}
            """));

        Assert.True(ev.AllDay);
        Assert.Null(ev.TimeZoneId);
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        // inclusive end == start for a single-day event
        Assert.Equal(new DateTimeOffset(2026, 12, 25, 0, 0, 0, TimeSpan.Zero), ev.EndUtc);
    }

    [Fact]
    public void Multi_day_all_day_subtracts_one_from_exclusive_end()
    {
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {"id":"d2","status":"confirmed","summary":"Trip",
             "start":{"date":"2026-07-01"},"end":{"date":"2026-07-04"}}
            """));

        Assert.True(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), ev.EndUtc); // 07-04 exclusive → 07-03 inclusive
    }

    [Fact]
    public void Recurring_master_keeps_rrule()
    {
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {"id":"m1","iCalUID":"m1@g","status":"confirmed","summary":"Weekly",
             "start":{"dateTime":"2026-06-01T10:00:00Z"},"end":{"dateTime":"2026-06-01T11:00:00Z"},
             "recurrence":["RRULE:FREQ=WEEKLY;COUNT=10","EXDATE:20260615T100000Z"]}
            """));

        Assert.NotNull(ev.Rrule);
        Assert.Contains("RRULE:FREQ=WEEKLY;COUNT=10", ev.Rrule);
        Assert.Contains("EXDATE:20260615T100000Z", ev.Rrule);
        Assert.Null(ev.RecurrenceId); // a master, not an override
    }

    [Fact]
    public void Moved_occurrence_is_an_override_keyed_by_original_start()
    {
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {"id":"m1_20260608T100000Z","iCalUID":"m1@g","status":"confirmed","summary":"Weekly (moved)",
             "recurringEventId":"m1",
             "originalStartTime":{"dateTime":"2026-06-08T10:00:00Z"},
             "start":{"dateTime":"2026-06-08T14:00:00Z"},"end":{"dateTime":"2026-06-08T15:00:00Z"}}
            """));

        Assert.Equal("m1@g", ev.Uid); // shares the series iCalUID
        Assert.NotNull(ev.RecurrenceId);
        Assert.Equal("2026-06-08T10:00:00.0000000Z", ev.RecurrenceId);
    }

    [Fact]
    public void Cancelled_event_maps_to_cancelled_status()
    {
        var ev = GoogleNormalizer.NormalizeEvent(Parse("""
            {"id":"gone1","status":"cancelled"}
            """));

        Assert.Equal(EventStatus.Cancelled, ev.Status);
        Assert.Equal("gone1", ev.RemoteId);
        Assert.Equal("gone1", ev.Uid); // no iCalUID on a tombstone → fall back to id
    }
}
