using Calendar.Plugin.Abstractions;
using Calendar.Plugin.Ics;

namespace Calendar.Plugin.Ics.Tests;

public class IcsNormalizerTests
{
    private const string TimedEvent = """
        BEGIN:VCALENDAR
        VERSION:2.0
        X-WR-CALNAME:Team Calendar
        BEGIN:VEVENT
        UID:evt-1@test
        SUMMARY:Standup
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        SEQUENCE:2
        LOCATION:Berlin HQ
        CATEGORIES:Work
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Parses_calendar_name_and_a_timed_event()
    {
        var result = IcsNormalizer.Parse(TimedEvent);

        Assert.Equal("Team Calendar", result.CalendarName);
        var ev = Assert.Single(result.Events);
        Assert.Equal("evt-1@test", ev.Uid);
        Assert.Equal("Standup", ev.Title);
        Assert.False(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero), ev.EndUtc);
        Assert.Equal("Berlin HQ", ev.Location);
        Assert.Contains("Work", ev.Categories);
        Assert.Equal(EventStatus.Confirmed, ev.Status);
    }

    [Fact]
    public void All_day_event_keeps_date_and_makes_dtend_inclusive()
    {
        // RFC 5545: DTEND 20260704 is exclusive → an event spanning 1–3 July.
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:trip@test
            SUMMARY:Long Weekend
            DTSTART;VALUE=DATE:20260701
            DTEND;VALUE=DATE:20260704
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(IcsNormalizer.Parse(ics).Events);

        Assert.True(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), ev.EndUtc); // inclusive
    }

    [Fact]
    public void Recurring_master_carries_the_rrule()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly@test
            SUMMARY:Weekly Sync
            DTSTART:20260106T100000Z
            DTEND:20260106T103000Z
            RRULE:FREQ=WEEKLY;BYDAY=TU
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(IcsNormalizer.Parse(ics).Events);

        Assert.NotNull(ev.Rrule);
        Assert.Contains("FREQ=WEEKLY", ev.Rrule);
        Assert.Null(ev.RecurrenceId);
    }

    [Fact]
    public void Recurrence_override_is_keyed_by_uid_and_recurrence_id()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly@test
            SUMMARY:Weekly Sync (moved)
            RECURRENCE-ID:20260113T100000Z
            DTSTART:20260113T110000Z
            DTEND:20260113T113000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(IcsNormalizer.Parse(ics).Events);

        Assert.NotNull(ev.RecurrenceId);
        Assert.StartsWith("weekly@test|", ev.RemoteId);
        Assert.Null(ev.Rrule);
    }

    [Fact]
    public void Cancelled_status_is_mapped()
    {
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:gone@test
            SUMMARY:Cancelled Thing
            DTSTART:20260610T090000Z
            STATUS:CANCELLED
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(IcsNormalizer.Parse(ics).Events);
        Assert.Equal(EventStatus.Cancelled, ev.Status);
    }

    [Fact]
    public void Force_category_is_added_for_carrier_presets()
    {
        var ev = Assert.Single(IcsNormalizer.Parse(TimedEvent, forceCategory: "Travel").Events);
        Assert.Contains("Travel", ev.Categories);
        Assert.Contains("Work", ev.Categories);
    }

    [Fact]
    public void Event_without_a_uid_still_gets_a_non_empty_id()
    {
        // Ical.Net assigns a UID when the source omits one, so we always have an addressable id.
        const string ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:No Uid
            DTSTART:20260610T090000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(IcsNormalizer.Parse(ics).Events);
        Assert.False(string.IsNullOrWhiteSpace(ev.Uid));
        Assert.Equal(ev.Uid, ev.RemoteId);
    }
}
