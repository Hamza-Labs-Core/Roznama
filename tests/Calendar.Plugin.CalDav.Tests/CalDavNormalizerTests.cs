using Calendar.Plugin.Abstractions;
using Calendar.Plugin.CalDav;

namespace Calendar.Plugin.CalDav.Tests;

/// <summary>Pure normalization tests over canned calendar-data bodies — no network (caldav-plugin.md §8, §13).</summary>
public class CalDavNormalizerTests
{
    private const string Href = "/123/calendars/home/abc.ics";

    [Fact]
    public void Timed_event_maps_to_utc_with_etag_change_tag()
    {
        var ical = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:evt-1@test
            SUMMARY:Standup
            DTSTART:20260610T090000Z
            DTEND:20260610T093000Z
            LOCATION:Room 4
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(CalDavNormalizer.Parse(ical, Href, "\"etag-1\""));

        Assert.Equal(Href, ev.RemoteId);
        Assert.Equal("evt-1@test", ev.Uid);
        Assert.Equal("Standup", ev.Title);
        Assert.False(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero), ev.EndUtc);
        Assert.Equal("Room 4", ev.Location);
        Assert.Equal("\"etag-1\"", ev.ChangeTag);
        Assert.Equal(EventStatus.Confirmed, ev.Status);
    }

    [Fact]
    public void All_day_event_stores_inclusive_end_and_allday_flag()
    {
        // RFC 5545 all-day DTEND is exclusive (12th) → inclusive end is the 11th.
        var ical = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:trip@test
            SUMMARY:Conference
            DTSTART;VALUE=DATE:20260610
            DTEND;VALUE=DATE:20260612
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(CalDavNormalizer.Parse(ical, Href, "\"e\""));

        Assert.True(ev.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), ev.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero), ev.EndUtc);
    }

    [Fact]
    public void Recurrence_master_and_override_become_separate_rows()
    {
        var ical = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:weekly@test
            SUMMARY:Weekly sync
            DTSTART:20260601T100000Z
            DTEND:20260601T103000Z
            RRULE:FREQ=WEEKLY;COUNT=10
            END:VEVENT
            BEGIN:VEVENT
            UID:weekly@test
            RECURRENCE-ID:20260608T100000Z
            SUMMARY:Weekly sync (moved)
            DTSTART:20260608T110000Z
            DTEND:20260608T113000Z
            END:VEVENT
            END:VCALENDAR
            """;

        var events = CalDavNormalizer.Parse(ical, Href, "\"e\"");

        Assert.Equal(2, events.Count);
        var master = events.Single(e => e.RecurrenceId is null);
        var ovr = events.Single(e => e.RecurrenceId is not null);

        Assert.NotNull(master.Rrule);
        Assert.Contains("FREQ=WEEKLY", master.Rrule);
        Assert.Equal(Href, master.RemoteId);

        // The override is keyed by (href, recurrence-id) so it does not collide with the master.
        Assert.StartsWith(Href + "|", ovr.RemoteId);
        Assert.Equal("Weekly sync (moved)", ovr.Title);
        Assert.Equal("weekly@test", ovr.Uid);
    }

    [Fact]
    public void Cancelled_status_is_mapped_for_tombstoning()
    {
        var ical = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:gone@test
            SUMMARY:Cancelled meeting
            DTSTART:20260610T090000Z
            DTEND:20260610T093000Z
            STATUS:CANCELLED
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(CalDavNormalizer.Parse(ical, Href, "\"e\""));
        Assert.Equal(EventStatus.Cancelled, ev.Status);
    }

    [Fact]
    public void Geo_and_categories_are_carried_through()
    {
        var ical = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:geo@test
            SUMMARY:Lunch
            DTSTART:20260610T120000Z
            DTEND:20260610T130000Z
            GEO:48.8584;2.2945
            CATEGORIES:Personal,Food
            END:VEVENT
            END:VCALENDAR
            """;

        var ev = Assert.Single(CalDavNormalizer.Parse(ical, Href, "\"e\""));

        Assert.NotNull(ev.Geo);
        Assert.Equal(48.8584, ev.Geo!.Value.Lat, 4);
        Assert.Equal(2.2945, ev.Geo!.Value.Lng, 4);
        Assert.Contains("Personal", ev.Categories);
        Assert.Contains("Food", ev.Categories);
    }
}
