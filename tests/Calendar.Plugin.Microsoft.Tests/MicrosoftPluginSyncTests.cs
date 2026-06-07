using System.Net;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Plugin.Microsoft.Tests;

/// <summary>
/// Microsoft Graph plugin behavior against a stub HTTP API + a fake bearer broker (microsoft-graph-plugin.md
/// §14). Covers calendar listing, the first (full) windowed delta, paging via <c>@odata.nextLink</c>,
/// persisting + replaying <c>@odata.deltaLink</c> (incremental round), <c>@removed</c> tombstones → delete,
/// recurrence master + exception, a <c>410</c> → <see cref="SyncResetRequiredException"/>, and the
/// <c>Prefer</c> headers (UTC + maxpagesize) on every delta request.
/// </summary>
public class MicrosoftPluginSyncTests
{
    private const string CalId = "AAA=";

    // ── ListCalendars ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCalendars_maps_calendars_and_applies_bearer()
    {
        var stub = new GraphApiStub().On("/me/calendars?", HttpStatusCode.OK, """
            {"value":[
              {"id":"AAA=","name":"Calendar","hexColor":"#1a73e8","canEdit":true,"isDefaultCalendar":true},
              {"id":"BBB=","name":"Team (shared)","color":"lightBlue","canEdit":false}
            ]}
            """);
        var plugin = await BuildAsync(stub);

        var calendars = await plugin.ListCalendarsAsync(CancellationToken.None);

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.RemoteId == "AAA=" && !c.IsReadOnly && c.Color == "#1a73e8");
        Assert.Contains(calendars, c => c.RemoteId == "BBB=" && c.IsReadOnly);
        Assert.All(stub.Requests, r => Assert.Equal("Bearer canned-access-token", r.Authorization));
    }

    [Fact]
    public async Task ListCalendars_follows_odata_nextLink()
    {
        var stub = new GraphApiStub()
            .On("$skiptoken=CALPAGE2", HttpStatusCode.OK,
                """{"value":[{"id":"CCC=","name":"More","canEdit":true}]}""")
            .On("/me/calendars?", HttpStatusCode.OK, """
                {"value":[{"id":"AAA=","name":"Calendar","canEdit":true}],
                 "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendars?$skiptoken=CALPAGE2"}
                """);
        var plugin = await BuildAsync(stub);

        var calendars = await plugin.ListCalendarsAsync(CancellationToken.None);

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.RemoteId == "AAA=");
        Assert.Contains(calendars, c => c.RemoteId == "CCC=");
    }

    // ── First (full) windowed delta ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_sync_uses_window_sets_prefer_headers_and_returns_events_plus_deltaLink()
    {
        var stub = new GraphApiStub().On("calendarView/delta", HttpStatusCode.OK, """
            {"value":[
              {"id":"EV1","iCalUId":"u1","subject":"Alpha","type":"singleInstance","isCancelled":false,
               "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
               "end":{"dateTime":"2026-06-10T10:00:00.0000000","timeZone":"UTC"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DELTA_1"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalId, syncToken: null, CancellationToken.None);

        var upsert = Assert.Single(result.Upserts);
        Assert.Equal("EV1", upsert.RemoteId);
        Assert.Empty(result.Deletes);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DELTA_1",
            result.NewSyncToken);

        // The initial round targets the scoped calendar path with a window and NO $select / deltatoken.
        var url = stub.Requests[0].Uri.ToString();
        Assert.Contains("/me/calendars/AAA%3D/calendarView/delta", url);
        Assert.Contains("startDateTime=", url);
        Assert.Contains("endDateTime=", url);
        Assert.DoesNotContain("$select", url);
        Assert.DoesNotContain("$deltatoken", url);

        // Prefer headers force UTC and cap page size on the delta request.
        var prefer = stub.Requests[0].Prefer;
        Assert.NotNull(prefer);
        Assert.Contains("outlook.timezone=\"UTC\"", prefer);
        Assert.Contains("odata.maxpagesize=50", prefer);
    }

    [Fact]
    public async Task First_sync_follows_nextLink_then_persists_deltaLink_from_terminal_page()
    {
        var stub = new GraphApiStub()
            // Page 2 (terminal): carries the deltaLink.
            .On("$skiptoken=SKIP2", HttpStatusCode.OK, """
                {"value":[
                  {"id":"EV2","iCalUId":"u2","subject":"Beta","type":"singleInstance",
                   "start":{"dateTime":"2026-06-11T09:00:00.0000000","timeZone":"UTC"},
                   "end":{"dateTime":"2026-06-11T10:00:00.0000000","timeZone":"UTC"}}
                ],
                "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DELTA_FINAL"}
                """)
            // Page 1: only a nextLink (an intermediate page must NOT carry a deltaLink).
            .On("calendarView/delta", HttpStatusCode.OK, """
                {"value":[
                  {"id":"EV1","iCalUId":"u1","subject":"Alpha","type":"singleInstance",
                   "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
                   "end":{"dateTime":"2026-06-10T10:00:00.0000000","timeZone":"UTC"}}
                ],
                "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$skiptoken=SKIP2"}
                """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalId, null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.EndsWith("$deltatoken=DELTA_FINAL", result.NewSyncToken);
        Assert.Equal(2, stub.Requests.Count);
    }

    // ── Incremental round (deltaLink round-trip) ──────────────────────────────────────────────────────

    [Fact]
    public async Task Incremental_sync_gets_saved_deltaLink_verbatim_and_handles_removed_tombstone()
    {
        var deltaLink =
            "https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DELTA_1";
        var stub = new GraphApiStub().On("$deltatoken=DELTA_1", HttpStatusCode.OK, """
            {"value":[
              {"id":"EV1","iCalUId":"u1","subject":"Alpha (edited)","type":"singleInstance",
               "start":{"dateTime":"2026-06-10T09:30:00.0000000","timeZone":"UTC"},
               "end":{"dateTime":"2026-06-10T10:30:00.0000000","timeZone":"UTC"}},
              {"id":"EV9","@removed":{"reason":"deleted"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DELTA_2"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalId, deltaLink, CancellationToken.None);

        var upsert = Assert.Single(result.Upserts);
        Assert.Equal("EV1", upsert.RemoteId);
        Assert.Equal("Alpha (edited)", upsert.Title);

        var delete = Assert.Single(result.Deletes); // @removed tombstone → delete
        Assert.Equal("EV9", delete);

        Assert.EndsWith("$deltatoken=DELTA_2", result.NewSyncToken);

        // The incremental round GETs the saved deltaLink verbatim — no fresh startDateTime/endDateTime window.
        var url = stub.Requests[0].Uri.ToString();
        Assert.Contains("$deltatoken=DELTA_1", url);
        Assert.DoesNotContain("startDateTime=", url);
        Assert.DoesNotContain("endDateTime=", url);
    }

    [Fact]
    public async Task Cancelled_event_in_delta_routes_to_delete()
    {
        var stub = new GraphApiStub().On("calendarView/delta", HttpStatusCode.OK, """
            {"value":[
              {"id":"EVX","iCalUId":"ux","subject":"Cancelled mtg","type":"singleInstance","isCancelled":true,
               "start":{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"},
               "end":{"dateTime":"2026-06-10T10:00:00.0000000","timeZone":"UTC"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=D"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalId, null, CancellationToken.None);

        Assert.Empty(result.Upserts);
        Assert.Equal("EVX", Assert.Single(result.Deletes));
    }

    [Fact]
    public async Task Recurrence_master_and_exception_both_upsert()
    {
        var stub = new GraphApiStub().On("calendarView/delta", HttpStatusCode.OK, """
            {"value":[
              {"id":"M1","iCalUId":"m1","subject":"Weekly","type":"seriesMaster",
               "start":{"dateTime":"2026-06-01T10:00:00.0000000","timeZone":"UTC"},
               "end":{"dateTime":"2026-06-01T11:00:00.0000000","timeZone":"UTC"},
               "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday"]},
                             "range":{"type":"numbered","numberOfOccurrences":10,"startDate":"2026-06-01"}}},
              {"id":"EX1","iCalUId":"ex1","subject":"Weekly (moved)","type":"exception","seriesMasterId":"M1",
               "start":{"dateTime":"2026-06-08T14:00:00.0000000","timeZone":"UTC"},
               "end":{"dateTime":"2026-06-08T15:00:00.0000000","timeZone":"UTC"}}
            ],
            "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=DR"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(CalId, null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        var master = Assert.Single(result.Upserts, e => e.RemoteId == "M1");
        Assert.NotNull(master.Rrule);
        Assert.Null(master.RecurrenceId);

        var exception = Assert.Single(result.Upserts, e => e.RemoteId == "EX1");
        Assert.Null(exception.Rrule);
        Assert.NotNull(exception.RecurrenceId);
    }

    // ── 410 → reset ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_deltaLink_410_throws_SyncResetRequired()
    {
        var staleLink =
            "https://graph.microsoft.com/v1.0/me/calendars/AAA=/calendarView/delta?$deltatoken=STALE";
        var stub = new GraphApiStub().On("$deltatoken=STALE", HttpStatusCode.Gone,
            """{"error":{"code":"syncStateNotFound","message":"The sync state is not found."}}""");
        var plugin = await BuildAsync(stub);

        await Assert.ThrowsAsync<SyncResetRequiredException>(
            () => plugin.SyncAsync(CalId, staleLink, CancellationToken.None));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<MicrosoftPlugin> BuildAsync(GraphApiStub stub)
    {
        var client = new HttpClient(stub);
        var host = new PluginHostServices(
            NullLogger.Instance,
            new FakeBearerAuthBroker(),
            new InMemoryPluginCache(),
            () => client,
            configJson: """{"syncWindowMonthsBack":1,"syncWindowMonthsForward":12}""");

        var plugin = new MicrosoftPlugin();
        await plugin.InitializeAsync(host, CancellationToken.None);
        return plugin;
    }
}
