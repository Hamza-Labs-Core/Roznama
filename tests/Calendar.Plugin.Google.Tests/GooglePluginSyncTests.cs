using System.Net;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Plugin.Google.Tests;

/// <summary>
/// Google plugin behavior against a stub HTTP API + a fake bearer broker (google §15). Covers calendar
/// listing (incl. Holidays/Birthdays), the first (full) sync, incremental delta via <c>nextSyncToken</c>,
/// cancelled → delete, recurrence master + override, pagination (token only on the last page), and a
/// <c>410</c> → <see cref="SyncResetRequiredException"/>.
/// </summary>
public class GooglePluginSyncTests
{
    private const string PrimaryId = "primary";

    // ── ListCalendars ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListCalendars_surfaces_primary_holidays_and_birthdays()
    {
        var stub = new GoogleApiStub().On("calendarList", HttpStatusCode.OK, """
            {"items":[
              {"id":"primary","summary":"Me","backgroundColor":"#fff","accessRole":"owner","primary":true},
              {"id":"en.usa#holiday@group.v.calendar.google.com","summary":"Holidays in United States","accessRole":"reader"},
              {"id":"addressbook#contacts@group.v.calendar.google.com","summary":"Birthdays","accessRole":"reader"}
            ]}
            """);
        var plugin = await BuildAsync(stub);

        var calendars = await plugin.ListCalendarsAsync(CancellationToken.None);

        Assert.Equal(3, calendars.Count);
        Assert.Contains(calendars, c => c.RemoteId == "primary" && !c.IsReadOnly);
        Assert.Contains(calendars, c => c.Name == "Holidays in United States" && c.IsReadOnly);
        Assert.Contains(calendars, c => c.Name == "Birthdays" && c.IsReadOnly);

        // The broker applied a bearer on every request.
        Assert.All(stub.Requests, r => Assert.Equal("Bearer canned-access-token", r.Authorization));
    }

    [Fact]
    public async Task ListCalendars_follows_pageToken()
    {
        var stub = new GoogleApiStub()
            .On("calendarList?maxResults=250&showHidden=true&pageToken=PAGE2", HttpStatusCode.OK,
                """{"items":[{"id":"c2","summary":"Second","accessRole":"writer"}]}""")
            // The page-1 route is registered second so the more specific page-2 URL matches first.
            .On("calendarList", HttpStatusCode.OK,
                """{"items":[{"id":"c1","summary":"First","accessRole":"writer"}],"nextPageToken":"PAGE2"}""");
        var plugin = await BuildAsync(stub);

        var calendars = await plugin.ListCalendarsAsync(CancellationToken.None);

        Assert.Equal(2, calendars.Count);
        Assert.Contains(calendars, c => c.RemoteId == "c1");
        Assert.Contains(calendars, c => c.RemoteId == "c2");
    }

    // ── Full sync ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_sync_uses_time_window_and_returns_all_events_plus_token()
    {
        var stub = new GoogleApiStub().On("/events?", HttpStatusCode.OK, """
            {"items":[
              {"id":"e1","iCalUID":"e1@g","status":"confirmed","summary":"Alpha",
               "start":{"dateTime":"2026-06-10T09:00:00Z"},"end":{"dateTime":"2026-06-10T10:00:00Z"}},
              {"id":"e2","iCalUID":"e2@g","status":"confirmed","summary":"Beta",
               "start":{"date":"2026-06-11"},"end":{"date":"2026-06-12"}}
            ],"nextSyncToken":"SYNC_1"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(PrimaryId, syncToken: null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Empty(result.Deletes);
        Assert.Equal("SYNC_1", result.NewSyncToken);

        // Full sync carries the active window + singleEvents=false and NO syncToken.
        var url = stub.Requests[0].Uri.ToString();
        Assert.Contains("singleEvents=false", url);
        Assert.Contains("timeMin=", url);
        Assert.Contains("timeMax=", url);
        Assert.DoesNotContain("syncToken=", url);
    }

    // ── Incremental sync (syncToken round-trip) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Incremental_sync_sends_token_and_omits_time_bounds()
    {
        var stub = new GoogleApiStub()
            .On("syncToken=SYNC_1", HttpStatusCode.OK, """
                {"items":[
                  {"id":"e1","iCalUID":"e1@g","status":"confirmed","summary":"Alpha (edited)",
                   "start":{"dateTime":"2026-06-10T09:30:00Z"},"end":{"dateTime":"2026-06-10T10:30:00Z"}},
                  {"id":"e2","status":"cancelled"}
                ],"nextSyncToken":"SYNC_2"}
                """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(PrimaryId, "SYNC_1", CancellationToken.None);

        var upsert = Assert.Single(result.Upserts);
        Assert.Equal("e1", upsert.RemoteId);
        Assert.Equal("Alpha (edited)", upsert.Title);

        var delete = Assert.Single(result.Deletes); // cancelled tombstone → delete
        Assert.Equal("e2", delete);

        Assert.Equal("SYNC_2", result.NewSyncToken);

        var url = stub.Requests[0].Uri.ToString();
        Assert.Contains("syncToken=SYNC_1", url);
        Assert.DoesNotContain("timeMin=", url); // mutually exclusive with syncToken (google §5, §13)
        Assert.DoesNotContain("timeMax=", url);
    }

    [Fact]
    public async Task Recurrence_master_and_moved_override_both_upsert()
    {
        var stub = new GoogleApiStub().On("/events?", HttpStatusCode.OK, """
            {"items":[
              {"id":"m1","iCalUID":"m1@g","status":"confirmed","summary":"Weekly",
               "start":{"dateTime":"2026-06-01T10:00:00Z"},"end":{"dateTime":"2026-06-01T11:00:00Z"},
               "recurrence":["RRULE:FREQ=WEEKLY;COUNT=10"]},
              {"id":"m1_20260608T100000Z","iCalUID":"m1@g","status":"confirmed","summary":"Weekly (moved)",
               "recurringEventId":"m1","originalStartTime":{"dateTime":"2026-06-08T10:00:00Z"},
               "start":{"dateTime":"2026-06-08T14:00:00Z"},"end":{"dateTime":"2026-06-08T15:00:00Z"}}
            ],"nextSyncToken":"SYNC_R"}
            """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(PrimaryId, null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        var master = Assert.Single(result.Upserts, e => e.RemoteId == "m1");
        Assert.NotNull(master.Rrule);
        Assert.Null(master.RecurrenceId);

        var moved = Assert.Single(result.Upserts, e => e.RemoteId == "m1_20260608T100000Z");
        Assert.Equal("m1@g", moved.Uid);
        Assert.NotNull(moved.RecurrenceId);
    }

    // ── Pagination ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pagination_takes_sync_token_only_from_the_last_page()
    {
        var stub = new GoogleApiStub()
            .On("pageToken=P2", HttpStatusCode.OK, """
                {"items":[{"id":"e2","iCalUID":"e2@g","status":"confirmed","summary":"Beta",
                  "start":{"dateTime":"2026-06-11T09:00:00Z"},"end":{"dateTime":"2026-06-11T10:00:00Z"}}],
                 "nextSyncToken":"SYNC_FINAL"}
                """)
            // Page 1: only nextPageToken (an intermediate page must NOT carry a usable sync token).
            .On("/events?", HttpStatusCode.OK, """
                {"items":[{"id":"e1","iCalUID":"e1@g","status":"confirmed","summary":"Alpha",
                  "start":{"dateTime":"2026-06-10T09:00:00Z"},"end":{"dateTime":"2026-06-10T10:00:00Z"}}],
                 "nextPageToken":"P2"}
                """);
        var plugin = await BuildAsync(stub);

        var result = await plugin.SyncAsync(PrimaryId, null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Equal("SYNC_FINAL", result.NewSyncToken); // taken from the terminal page only
        Assert.Equal(2, stub.Requests.Count);
    }

    // ── 410 → reset ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stale_token_410_throws_SyncResetRequired()
    {
        var stub = new GoogleApiStub().On("syncToken=STALE", HttpStatusCode.Gone,
            """{"error":{"code":410,"message":"Sync token is no longer valid, a full sync is required."}}""");
        var plugin = await BuildAsync(stub);

        await Assert.ThrowsAsync<SyncResetRequiredException>(
            () => plugin.SyncAsync(PrimaryId, "STALE", CancellationToken.None));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<GooglePlugin> BuildAsync(GoogleApiStub stub)
    {
        var client = new HttpClient(stub);
        var host = new PluginHostServices(
            NullLogger.Instance,
            new FakeBearerAuthBroker(),
            new InMemoryPluginCache(),
            () => client,
            configJson: """{"syncWindowMonthsPast":12,"syncWindowMonthsFuture":24}""");

        var plugin = new GooglePlugin();
        await plugin.InitializeAsync(host, CancellationToken.None);
        return plugin;
    }
}
