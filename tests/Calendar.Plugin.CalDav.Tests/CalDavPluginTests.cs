using System.Net;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.CalDav;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Plugin.CalDav.Tests;

/// <summary>
/// CalDAV plugin behavior against a stub WebDAV server + fake Basic broker (caldav-plugin.md §13). Covers
/// discovery/listing, full + incremental (sync-collection) sync, deletes, recurrence, the CTag fallback,
/// a 410 sync-token invalidation → <see cref="SyncResetRequiredException"/>, and write-back create/update/
/// delete + a 412 → <see cref="ConcurrencyConflictException"/>.
/// </summary>
public class CalDavPluginTests
{
    private const string ServerUrl = "https://dav.test/dav/";
    private const string CalendarUrl = "https://dav.test/123/calendars/work/";

    // ── Discovery / listing ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCalendars_discovers_principal_home_and_collections()
    {
        var stub = new CalDavServerStub()
            .On("PROPFIND", "current-user-principal", HttpStatusCode.MultiStatus, PrincipalXml)
            .On("PROPFIND", "calendar-home-set", HttpStatusCode.MultiStatus, HomeSetXml)
            .On("PROPFIND", "supported-calendar-component-set", HttpStatusCode.MultiStatus, CollectionsXml);
        var plugin = await BuildAsync(stub);

        var calendars = await plugin.ListCalendarsAsync(CancellationToken.None);

        var work = Assert.Single(calendars);
        Assert.Equal(CalendarUrl, work.RemoteId);
        Assert.Equal("Work", work.Name);
        Assert.Equal("#FF5733", work.Color);
        Assert.False(work.IsReadOnly);

        // The broker applied Basic auth on every request.
        Assert.All(stub.Requests, r => Assert.StartsWith("Basic ", r.Authorization));
    }

    // ── Sync (sync-collection) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_sync_with_sync_collection_upserts_all_events_and_returns_token()
    {
        var stub = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.MultiStatus, SyncCollectionFullXml)
            .On("REPORT", "calendar-multiget", HttpStatusCode.MultiStatus, MultiGetTwoXml);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalendarUrl, syncToken: null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Empty(result.Deletes);
        Assert.StartsWith("sc:", result.NewSyncToken);
        Assert.Contains("sync/0042", result.NewSyncToken);

        // First sync-collection carried an empty token.
        var syncReq = stub.Requests.First(r => r.Body.Contains("sync-collection"));
        Assert.Contains("<d:sync-token></d:sync-token>", syncReq.Body);
    }

    [Fact]
    public async Task Incremental_sync_collection_emits_upsert_and_delete()
    {
        var stub = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.MultiStatus, SyncCollectionDeltaXml)
            .On("REPORT", "calendar-multiget", HttpStatusCode.MultiStatus, MultiGetOneXml);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalendarUrl, "sc:https://dav.test/123/calendars/work/sync/0042", CancellationToken.None);

        var upsert = Assert.Single(result.Upserts);
        Assert.Equal("A (updated)", upsert.Title);
        var delete = Assert.Single(result.Deletes);
        Assert.Equal("https://dav.test/123/calendars/work/b.ics", delete);
        Assert.Contains("sync/0043", result.NewSyncToken);

        // The prior token was replayed in the request.
        var syncReq = stub.Requests.First(r => r.Body.Contains("sync-collection"));
        Assert.Contains("sync/0042", syncReq.Body);
    }

    [Fact]
    public async Task Recurrence_master_and_override_both_arrive_via_sync_collection()
    {
        var stub = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.MultiStatus, SyncCollectionRecurringXml)
            .On("REPORT", "calendar-multiget", HttpStatusCode.MultiStatus, MultiGetRecurringXml);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalendarUrl, null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Contains(result.Upserts, e => e.RecurrenceId is null && e.Rrule is not null);
        Assert.Contains(result.Upserts, e => e.RecurrenceId is not null);
    }

    [Fact]
    public async Task Invalidated_sync_token_410_throws_SyncResetRequired()
    {
        var stub = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.Gone, null);
        var plugin = await BuildAsync(stub);

        await Assert.ThrowsAsync<SyncResetRequiredException>(() =>
            plugin.SyncAsync(CalendarUrl, "sc:stale-token", CancellationToken.None));
    }

    // ── CTag / ETag-diff fallback ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_falls_back_to_calendar_query_when_sync_collection_unsupported()
    {
        var stub = new CalDavServerStub()
            // Server rejects sync-collection (e.g. older Radicale) …
            .On("REPORT", "sync-collection", HttpStatusCode.BadRequest, null)
            // … so the plugin uses calendar-query + a CTag PROPFIND.
            .On("REPORT", "calendar-query", HttpStatusCode.MultiStatus, CalendarQueryTwoXml)
            .On("PROPFIND", "supported-calendar-component-set", HttpStatusCode.MultiStatus, CollectionsXml);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalendarUrl, syncToken: null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Empty(result.Deletes);
        Assert.StartsWith("ct:", result.NewSyncToken);
    }

    [Fact]
    public async Task CTag_fallback_reports_vanished_resource_as_delete_on_second_run()
    {
        var stub = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.BadRequest, null)
            .On("REPORT", "calendar-query", HttpStatusCode.MultiStatus, CalendarQueryTwoXml)
            .On("PROPFIND", "supported-calendar-component-set", HttpStatusCode.MultiStatus, CollectionsXml);
        var sharedCache = new InMemoryPluginCache();
        var plugin = await BuildAsync(stub, sharedCache);

        var first = await plugin.SyncAsync(CalendarUrl, null, CancellationToken.None);
        Assert.Equal(2, first.Upserts.Count);

        // Second run: only one resource remains → the other is a delete.
        var stub2 = new CalDavServerStub()
            .On("REPORT", "sync-collection", HttpStatusCode.BadRequest, null)
            .On("REPORT", "calendar-query", HttpStatusCode.MultiStatus, CalendarQueryOneXml)
            .On("PROPFIND", "supported-calendar-component-set", HttpStatusCode.MultiStatus, CollectionsXml);
        // Reuse the same cache so the prior snapshot is visible.
        var plugin2 = await BuildAsync(stub2, sharedCache);

        var second = await plugin2.SyncAsync(CalendarUrl, first.NewSyncToken, CancellationToken.None);
        Assert.Empty(second.Upserts); // 'a' unchanged (same etag)
        Assert.Equal("https://dav.test/123/calendars/work/b.ics", Assert.Single(second.Deletes));
    }

    // ── Write-back ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_puts_with_if_none_match_and_returns_etag()
    {
        var stub = new CalDavServerStub()
            .OnMethod("PUT", HttpStatusCode.Created, mutate: r => r.Headers.TryAddWithoutValidation("ETag", "\"new-1\""));
        var plugin = await BuildAsync(stub);

        var draft = Draft(uid: "fresh@test", title: "Created");
        var created = await plugin.CreateEventAsync(CalendarUrl, draft, CancellationToken.None);

        Assert.Equal("\"new-1\"", created.ChangeTag);
        Assert.Equal(CalendarUrl + "fresh@test.ics", created.RemoteId);

        var put = Assert.Single(stub.Requests, r => r.Method == "PUT");
        Assert.Equal("*", put.IfNoneMatch);
        Assert.Contains("BEGIN:VEVENT", put.Body);
        Assert.Contains("SUMMARY:Created", put.Body);
    }

    [Fact]
    public async Task UpdateEvent_sends_if_match_and_returns_new_etag()
    {
        var resourceUrl = CalendarUrl + "a.ics";
        var stub = new CalDavServerStub()
            .OnMethod("PUT", HttpStatusCode.NoContent, mutate: r => r.Headers.TryAddWithoutValidation("ETag", "\"a3\""));
        var plugin = await BuildAsync(stub);

        var draft = Draft(uid: "a@test", title: "Updated") with { RemoteId = resourceUrl };
        var updated = await plugin.UpdateEventAsync(CalendarUrl, draft, ifMatchChangeTag: "\"a2\"", CancellationToken.None);

        Assert.Equal("\"a3\"", updated.ChangeTag);
        var put = Assert.Single(stub.Requests, r => r.Method == "PUT");
        Assert.Equal("\"a2\"", put.IfMatch);
    }

    [Fact]
    public async Task UpdateEvent_412_throws_ConcurrencyConflict()
    {
        var stub = new CalDavServerStub()
            .OnMethod("PUT", HttpStatusCode.PreconditionFailed);
        var plugin = await BuildAsync(stub);

        var draft = Draft(uid: "a@test", title: "Stale") with { RemoteId = CalendarUrl + "a.ics" };

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            plugin.UpdateEventAsync(CalendarUrl, draft, ifMatchChangeTag: "\"old\"", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteEvent_sends_if_match()
    {
        var stub = new CalDavServerStub()
            .OnMethod("DELETE", HttpStatusCode.NoContent);
        var plugin = await BuildAsync(stub);

        await plugin.DeleteEventAsync(CalendarUrl, CalendarUrl + "a.ics", ifMatchChangeTag: "\"a2\"", CancellationToken.None);

        var del = Assert.Single(stub.Requests, r => r.Method == "DELETE");
        Assert.Equal("\"a2\"", del.IfMatch);
    }

    [Fact]
    public async Task DeleteEvent_412_throws_ConcurrencyConflict()
    {
        var stub = new CalDavServerStub()
            .OnMethod("DELETE", HttpStatusCode.PreconditionFailed);
        var plugin = await BuildAsync(stub);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            plugin.DeleteEventAsync(CalendarUrl, CalendarUrl + "a.ics", ifMatchChangeTag: "\"old\"", CancellationToken.None));
    }

    [Fact]
    public async Task Unauthorized_401_surfaces_app_password_guidance()
    {
        var stub = new CalDavServerStub()
            .On("PROPFIND", "current-user-principal", HttpStatusCode.Unauthorized, null);
        var plugin = await BuildAsync(stub);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            plugin.ListCalendarsAsync(CancellationToken.None));
        Assert.Contains("app-specific password", ex.Message);
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────

    private static async Task<CalDavPlugin> BuildAsync(CalDavServerStub stub, InMemoryPluginCache? sharedCache = null)
    {
        var client = new HttpClient(stub);
        var host = new PluginHostServices(
            NullLogger.Instance,
            new FakeBasicAuthBroker(),
            sharedCache ?? new InMemoryPluginCache(),
            () => client,
            configJson: $$"""{"serverUrl":"{{ServerUrl}}","username":"me@test"}""");

        var plugin = new CalDavPlugin();
        await plugin.InitializeAsync(host, CancellationToken.None);
        return plugin;
    }

    private static RemoteEvent Draft(string uid, string title) => new(
        RemoteId: string.Empty,
        Uid: uid,
        Title: title,
        StartUtc: new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
        EndUtc: new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
        TimeZoneId: null,
        AllDay: false,
        Rrule: null,
        RecurrenceId: null,
        Location: null,
        Geo: null,
        Categories: Array.Empty<string>(),
        ChangeTag: null,
        Status: EventStatus.Confirmed);

    // ── canned XML ──────────────────────────────────────────────────────────────────────────────────

    private const string PrincipalXml = """
        <d:multistatus xmlns:d="DAV:">
          <d:response>
            <d:href>/dav/</d:href>
            <d:propstat><d:prop><d:current-user-principal><d:href>/principals/users/me/</d:href></d:current-user-principal></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private const string HomeSetXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response>
            <d:href>/principals/users/me/</d:href>
            <d:propstat><d:prop><c:calendar-home-set><d:href>/123/calendars/</d:href></c:calendar-home-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
          </d:response>
        </d:multistatus>
        """;

    private const string CollectionsXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:cs="http://calendarserver.org/ns/" xmlns:ic="http://apple.com/ns/ical/">
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
        </d:multistatus>
        """;

    private const string SyncCollectionFullXml = """
        <d:multistatus xmlns:d="DAV:">
          <d:sync-token>https://dav.test/123/calendars/work/sync/0042</d:sync-token>
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/work/b.ics</d:href><d:propstat><d:prop><d:getetag>"b1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string SyncCollectionDeltaXml = """
        <d:multistatus xmlns:d="DAV:">
          <d:sync-token>https://dav.test/123/calendars/work/sync/0043</d:sync-token>
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a2"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/work/b.ics</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
        </d:multistatus>
        """;

    private const string SyncCollectionRecurringXml = """
        <d:multistatus xmlns:d="DAV:">
          <d:sync-token>https://dav.test/123/calendars/work/sync/0050</d:sync-token>
          <d:response><d:href>/123/calendars/work/r.ics</d:href><d:propstat><d:prop><d:getetag>"r1"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string MultiGetTwoXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:A
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/work/b.ics</d:href><d:propstat><d:prop><d:getetag>"b1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:b@test
        SUMMARY:B
        DTSTART:20260611T090000Z
        DTEND:20260611T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string MultiGetOneXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a2"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:A (updated)
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string MultiGetRecurringXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/123/calendars/work/r.ics</d:href><d:propstat><d:prop><d:getetag>"r1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:weekly@test
        SUMMARY:Weekly
        DTSTART:20260601T100000Z
        DTEND:20260601T103000Z
        RRULE:FREQ=WEEKLY;COUNT=10
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly@test
        RECURRENCE-ID:20260608T100000Z
        SUMMARY:Weekly (moved)
        DTSTART:20260608T110000Z
        DTEND:20260608T113000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string CalendarQueryTwoXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:A
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
          <d:response><d:href>/123/calendars/work/b.ics</d:href><d:propstat><d:prop><d:getetag>"b1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:b@test
        SUMMARY:B
        DTSTART:20260611T090000Z
        DTEND:20260611T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    private const string CalendarQueryOneXml = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/123/calendars/work/a.ics</d:href><d:propstat><d:prop><d:getetag>"a1"</d:getetag><c:calendar-data>BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:A
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        END:VEVENT
        END:VCALENDAR</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;
}
