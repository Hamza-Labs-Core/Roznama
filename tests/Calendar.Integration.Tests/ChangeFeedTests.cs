using System.Net;
using System.Text;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.Ics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Integration.Tests;

/// <summary>
/// The live-refresh feed behind <c>GET /sync/stream</c> (API.md, UI.md §9): fan-out to every listener,
/// non-blocking publish with bounded drop-oldest buffers, teardown on cancel — and the end-to-end hook:
/// a calendar sync that changes the stored set publishes <c>eventsChanged</c>.
/// </summary>
public sealed class ChangeFeedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-feed-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Every_listener_receives_a_published_event()
    {
        var feed = new ChangeFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var first = ListenForOneAsync(feed, cts.Token);
        var second = ListenForOneAsync(feed, cts.Token);
        await Task.Delay(50, cts.Token);                      // let both subscriptions attach.

        feed.Publish(ChangeEventTypes.EventsChanged, new { source = "test" });

        var a = await first;
        var b = await second;
        Assert.Equal(ChangeEventTypes.EventsChanged, a.Type);
        Assert.Contains("test", a.Json);
        Assert.Equal(ChangeEventTypes.EventsChanged, b.Type);
    }

    [Fact]
    public void Publishing_with_no_listeners_or_a_full_buffer_never_blocks()
    {
        var feed = new ChangeFeed();
        for (var i = 0; i < 1000; i++)
            feed.Publish(ChangeEventTypes.SyncProgress, new { i });   // no subscriber: instant no-ops.
    }

    [Fact]
    public async Task A_cancelled_listener_detaches_cleanly()
    {
        var feed = new ChangeFeed();
        using var cts = new CancellationTokenSource();
        var listener = ListenForOneAsync(feed, cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener);

        feed.Publish(ChangeEventTypes.EventsChanged);          // must not throw into the detached channel.
    }

    [Fact]
    public async Task A_sync_that_changes_events_publishes_eventsChanged()
    {
        const string feedUrl = "https://feeds.test/h.ics";
        using var db = new CalendarDbContext(
            new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.Migrate();

        var changeFeed = new ChangeFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = ListenForOneAsync(changeFeed, cts.Token);
        await Task.Delay(50, cts.Token);

        var accounts = BuildAccountService(db, changeFeed, feedUrl, """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:x@test
            SUMMARY:Thing
            DTSTART;VALUE=DATE:20260115
            END:VEVENT
            END:VCALENDAR
            """);
        await accounts.ConnectIcsAsync(feedUrl, "Feed", 60, null, CancellationToken.None);

        var ev = await received;
        Assert.Equal(ChangeEventTypes.EventsChanged, ev.Type);
        Assert.Contains("sync", ev.Json);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<ChangeEvent> ListenForOneAsync(IChangeFeed feed, CancellationToken ct)
    {
        await foreach (var ev in feed.ListenAsync(ct))
            return ev;
        throw new InvalidOperationException("Feed completed without an event.");
    }

    private static IAccountService BuildAccountService(
        CalendarDbContext db, IChangeFeed changeFeed, string feedUrl, string ics)
    {
        var registry = new PluginRegistry();
        var icsPlugin = new IcsPlugin();
        registry.Register(new PluginRegistration(
            icsPlugin.Manifest, new[] { Capability.CalendarRead }, PluginState.Running,
            new TestInstance(icsPlugin.Manifest.Id, icsPlugin)));

        var device = new DeviceProvider(db);
        var vault = new SecretVault(db);
        var geocodeAggregator = new Calendar.Infrastructure.Aggregation.GeocodeAggregator(
            registry,
            new Calendar.Infrastructure.Aggregation.InMemoryAggregationResultCache(),
            NullLogger<Calendar.Infrastructure.Aggregation.GeocodeAggregator>.Instance);
        var geocode = new GeocodeService(
            db, geocodeAggregator, registry, device, NullLogger<GeocodeService>.Instance);
        var hostFactory = new PluginHostServicesFactory(
            vault, new NoopAuthBrokerFactory(), new InMemoryPluginCache(),
            new HttpClient(new OneFeedHandler(feedUrl, ics)), NullLoggerFactory.Instance);
        var sync = new CalendarSyncService(
            db, registry, hostFactory, new DedupGrouper(db), device, geocode,
            NullLoggerFactory.Instance, changeFeed);
        return new AccountService(db, vault, sync, registry, device);
    }

    private sealed class OneFeedHandler : HttpMessageHandler
    {
        private readonly string _url;
        private readonly string _body;
        public OneFeedHandler(string url, string body) => (_url, _body) = (url, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.ToString().Equals(_url, StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "text/calendar"),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
