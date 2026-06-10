using System.Net;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.Ics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Plugin.Ics.Tests;

public class IcsPluginSyncTests
{
    private const string FeedUrl = "https://feeds.test/cal.ics";

    private const string TwoEvents = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:Alpha
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        SEQUENCE:1
        END:VEVENT
        BEGIN:VEVENT
        UID:b@test
        SUMMARY:Beta
        DTSTART:20260611T090000Z
        DTEND:20260611T093000Z
        SEQUENCE:1
        END:VEVENT
        END:VCALENDAR
        """;

    private const string OneEvent = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a@test
        SUMMARY:Alpha
        DTSTART:20260610T090000Z
        DTEND:20260610T093000Z
        SEQUENCE:1
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public async Task First_sync_returns_all_events_as_upserts_with_a_token()
    {
        var handler = new QueueHandler();
        handler.Enqueue(Ok(TwoEvents, etag: "\"v1\""));
        var plugin = await BuildAsync(handler);

        var result = await plugin.SyncAsync(FeedUrl, syncToken: null, CancellationToken.None);

        Assert.Equal(2, result.Upserts.Count);
        Assert.Empty(result.Deletes);
        Assert.StartsWith("\"v1\"|", result.NewSyncToken);
    }

    [Fact]
    public async Task Unchanged_feed_returns_304_with_an_empty_delta()
    {
        var handler = new QueueHandler();
        handler.Enqueue(Ok(TwoEvents, etag: "\"v1\""));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var plugin = await BuildAsync(handler);

        var first = await plugin.SyncAsync(FeedUrl, null, CancellationToken.None);
        var second = await plugin.SyncAsync(FeedUrl, first.NewSyncToken, CancellationToken.None);

        Assert.Empty(second.Upserts);
        Assert.Empty(second.Deletes);
        Assert.Equal(first.NewSyncToken, second.NewSyncToken);
        // The conditional request carried the validator from the prior token.
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Fact]
    public async Task A_vanished_event_is_reported_as_a_delete()
    {
        var handler = new QueueHandler();
        handler.Enqueue(Ok(TwoEvents, etag: "\"v1\""));
        handler.Enqueue(Ok(OneEvent, etag: "\"v2\""));
        var plugin = await BuildAsync(handler);

        await plugin.SyncAsync(FeedUrl, null, CancellationToken.None);
        var second = await plugin.SyncAsync(FeedUrl, "\"v1\"||", CancellationToken.None);

        var deleted = Assert.Single(second.Deletes);
        Assert.Equal("b@test", deleted);
        Assert.Empty(second.Upserts); // Alpha is unchanged
    }

    [Fact]
    public async Task ListCalendars_synthesizes_one_read_only_calendar()
    {
        var handler = new QueueHandler();
        handler.Enqueue(Ok("""
            BEGIN:VCALENDAR
            X-WR-CALNAME:Holidays
            END:VCALENDAR
            """, etag: "\"v1\""));
        var plugin = await BuildAsync(handler);

        var calendar = Assert.Single(await plugin.ListCalendarsAsync(CancellationToken.None));

        Assert.Equal(FeedUrl, calendar.RemoteId);
        Assert.Equal("Holidays", calendar.Name);
        Assert.True(calendar.IsReadOnly);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<IcsPlugin> BuildAsync(QueueHandler handler)
    {
        var client = new HttpClient(handler);
        var host = new PluginHostServices(
            NullLogger.Instance,
            new NoopAuthBroker(),
            new InMemoryPluginCache(),
            () => client,
            configJson: $$"""{"feedUrl":"{{FeedUrl}}","refreshMinutes":60}""");

        var plugin = new IcsPlugin();
        await plugin.InitializeAsync(host, CancellationToken.None);
        return plugin;
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Ok(string body, string etag) => _ =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/calendar"),
        };
        response.Headers.TryAddWithoutValidation("ETag", etag);
        return response;
    };

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var factory = _responses.Dequeue();
            return Task.FromResult(factory(request));
        }
    }
}
