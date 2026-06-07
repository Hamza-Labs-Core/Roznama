using Calendar.Plugin.CalDav;

namespace Calendar.Plugin.CalDav.Tests;

/// <summary>Pure WebDAV/CalDAV 207 Multi-Status parsing tests over canned XML (caldav-plugin.md §4, §5, §7, §10).</summary>
public class WebDavParsingTests
{
    [Fact]
    public void Current_user_principal_is_extracted_from_a_207()
    {
        var xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/</d:href>
                <d:propstat>
                  <d:prop><d:current-user-principal><d:href>/principals/users/me/</d:href></d:current-user-principal></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        Assert.Equal("/principals/users/me/", WebDav.ParseCurrentUserPrincipal(xml));
    }

    [Fact]
    public void Calendar_home_set_is_extracted_from_a_207()
    {
        var xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/principals/users/me/</d:href>
                <d:propstat>
                  <d:prop><c:calendar-home-set><d:href>/123/calendars/</d:href></c:calendar-home-set></d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        Assert.Equal("/123/calendars/", WebDav.ParseCalendarHomeSet(xml));
    }

    [Fact]
    public void Calendar_collections_keep_only_vevent_calendars_and_drop_alpha_color()
    {
        var xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:ic="http://apple.com/ns/ical/">
              <d:response>
                <d:href>/123/calendars/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/123/calendars/work/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:displayname>Work</d:displayname>
                    <ic:calendar-color>#FF5733FF</ic:calendar-color>
                    <cs:getctag>ctag-100</cs:getctag>
                    <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/123/calendars/tasks/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:displayname>Tasks</d:displayname>
                    <c:supported-calendar-component-set><c:comp name="VTODO"/></c:supported-calendar-component-set>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var collections = WebDav.ParseCalendarCollections(xml);

        var work = Assert.Single(collections); // home collection + VTODO-only calendar are excluded
        Assert.Equal("/123/calendars/work/", work.Href);
        Assert.Equal("Work", work.Name);
        Assert.Equal("#FF5733", work.Color); // alpha byte dropped
        Assert.Equal("ctag-100", work.CTag);
    }

    [Fact]
    public void Multi_status_partial_does_not_leak_data_from_a_404_propstat()
    {
        // displayname resolved (200) but calendar-color is absent (404) — the good prop survives, the bad one is null.
        var xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:ic="http://apple.com/ns/ical/">
              <d:response>
                <d:href>/123/calendars/work/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype><d:collection/><c:calendar/></d:resourcetype>
                    <d:displayname>Work</d:displayname>
                    <c:supported-calendar-component-set><c:comp name="VEVENT"/></c:supported-calendar-component-set>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
                <d:propstat>
                  <d:prop><ic:calendar-color/></d:prop>
                  <d:status>HTTP/1.1 404 Not Found</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var work = Assert.Single(WebDav.ParseCalendarCollections(xml));
        Assert.Equal("Work", work.Name);
        Assert.Null(work.Color);
    }

    [Fact]
    public void Sync_collection_splits_changed_and_removed_and_returns_new_token()
    {
        var xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:sync-token>https://server/cal/sync/0043</d:sync-token>
              <d:response>
                <d:href>/123/calendars/work/a.ics</d:href>
                <d:propstat><d:prop><d:getetag>"a2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
              </d:response>
              <d:response>
                <d:href>/123/calendars/work/b.ics</d:href>
                <d:status>HTTP/1.1 404 Not Found</d:status>
              </d:response>
            </d:multistatus>
            """;

        var result = WebDav.ParseSyncCollection(xml);

        Assert.Equal("https://server/cal/sync/0043", result.NewSyncToken);
        var changed = Assert.Single(result.Changed);
        Assert.Equal("/123/calendars/work/a.ics", changed.Href);
        Assert.Equal("\"a2\"", changed.ETag);
        Assert.Equal("/123/calendars/work/b.ics", Assert.Single(result.Removed));
    }

    [Fact]
    public void Multiget_resources_yield_href_etag_and_calendar_data()
    {
        var xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/123/calendars/work/a.ics</d:href>
                <d:propstat>
                  <d:prop>
                    <d:getetag>"a2"</d:getetag>
                    <c:calendar-data>BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:a@test
            SUMMARY:A
            DTSTART:20260610T090000Z
            DTEND:20260610T093000Z
            END:VEVENT
            END:VCALENDAR</c:calendar-data>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var resource = Assert.Single(WebDav.ParseResources(xml));
        Assert.Equal("/123/calendars/work/a.ics", resource.Href);
        Assert.Equal("\"a2\"", resource.ETag);
        Assert.NotNull(resource.CalendarData);
        Assert.Contains("UID:a@test", resource.CalendarData);
    }
}
