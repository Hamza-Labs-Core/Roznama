using System.Net;
using System.Text;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.Ics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 3 scheduled sync engine: the sweep syncs due accounts, reschedules them, backs off exponentially
/// per account on failure (one provider's outage never throttles the rest), and recovers cleanly.
/// </summary>
public sealed class SyncSchedulerTests : IDisposable
{
    private const string Feed = "https://feeds.test/holidays.ics";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-sched-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Sweep_syncs_a_due_account_then_skips_it_until_the_next_run()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var handler = new MutableMapHandler { [Feed] = SingleDay("ny@test", "New Year", "20260101") };
        var (accounts, scheduler) = BuildServices(db, handler);

        var accountId = await accounts.ConnectIcsAsync(Feed, "Holidays", 60, null, CancellationToken.None);

        // Connect synced via the sync service directly — no account-level schedule row yet, so it's due.
        var first = await scheduler.SweepAsync(force: false, CancellationToken.None);
        Assert.Equal(1, first.Synced);
        Assert.Equal(0, first.Failed);

        var state = await AccountStateAsync(db, accountId);
        Assert.Equal(SyncRunState.Idle, state.State);
        Assert.Equal(0, state.Attempts);
        Assert.Null(state.BackoffUntilUtc);
        Assert.NotNull(state.NextRunAtUtc);
        Assert.True(state.NextRunAtUtc > DateTimeOffset.UtcNow.AddMinutes(25));    // ≈ now + 30m interval

        // Freshly rescheduled → the immediate next sweep skips it.
        var second = await scheduler.SweepAsync(force: false, CancellationToken.None);
        Assert.Equal(0, second.Synced);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task Failure_backs_off_exponentially_and_sits_out_sweeps_until_the_deadline()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var handler = new MutableMapHandler { [Feed] = SingleDay("ny@test", "New Year", "20260101") };
        var (accounts, scheduler) = BuildServices(db, handler);

        var accountId = await accounts.ConnectIcsAsync(Feed, "Holidays", 60, null, CancellationToken.None);
        handler.Remove(Feed);                                       // the feed starts failing (404)

        var sweep = await scheduler.SweepAsync(force: true, CancellationToken.None);
        Assert.Equal(1, sweep.Failed);

        var state = await AccountStateAsync(db, accountId);
        Assert.Equal(SyncRunState.Backoff, state.State);
        Assert.Equal(1, state.Attempts);
        Assert.NotNull(state.LastError);
        var firstDelay = state.BackoffUntilUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(firstDelay, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(70));   // ≈ 1m base

        // While in backoff a normal sweep skips the account entirely.
        var during = await scheduler.SweepAsync(force: false, CancellationToken.None);
        Assert.Equal(1, during.Skipped);
        Assert.Equal(0, during.Failed);

        // Expire the backoff: the next failure doubles the delay (base × 2^(attempts−1)).
        state.BackoffUntilUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.NextRunAtUtc = state.BackoffUntilUtc;
        await db.SaveChangesAsync();

        await scheduler.SweepAsync(force: false, CancellationToken.None);
        state = await AccountStateAsync(db, accountId);
        Assert.Equal(2, state.Attempts);
        var secondDelay = state.BackoffUntilUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(secondDelay, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(130)); // ≈ 2m
    }

    [Fact]
    public async Task Recovery_clears_backoff_and_flips_the_account_back_to_connected()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var handler = new MutableMapHandler { [Feed] = SingleDay("ny@test", "New Year", "20260101") };
        var (accounts, scheduler) = BuildServices(db, handler, new SyncSchedulerOptions { ErrorAfterAttempts = 1 });

        var accountId = await accounts.ConnectIcsAsync(Feed, "Holidays", 60, null, CancellationToken.None);

        handler.Remove(Feed);
        await scheduler.SweepAsync(force: true, CancellationToken.None);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        Assert.Equal(AccountStatus.Error, account.Status);          // ErrorAfterAttempts = 1 hit immediately

        handler[Feed] = SingleDay("ny@test", "New Year", "20260101");
        var sweep = await scheduler.SweepAsync(force: true, CancellationToken.None);
        Assert.Equal(1, sweep.Synced);

        var state = await AccountStateAsync(db, accountId);
        Assert.Equal(SyncRunState.Idle, state.State);
        Assert.Equal(0, state.Attempts);
        Assert.Null(state.BackoffUntilUtc);
        Assert.Null(state.LastError);
        Assert.Equal(AccountStatus.Connected, (await db.Accounts.FirstAsync(a => a.Id == accountId)).Status);
    }

    [Fact]
    public async Task Accounts_needing_user_action_sit_out_the_sweep()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var handler = new MutableMapHandler { [Feed] = SingleDay("ny@test", "New Year", "20260101") };
        var (accounts, scheduler) = BuildServices(db, handler);

        var accountId = await accounts.ConnectIcsAsync(Feed, "Holidays", 60, null, CancellationToken.None);
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        account.Status = AccountStatus.NeedsAuth;
        await db.SaveChangesAsync();

        var sweep = await scheduler.SweepAsync(force: true, CancellationToken.None);
        Assert.Equal(0, sweep.Accounts);
        Assert.Equal(0, sweep.Synced);
    }

    [Fact]
    public async Task SyncNow_returns_null_for_an_unknown_account_and_a_summary_for_a_real_one()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var handler = new MutableMapHandler { [Feed] = SingleDay("ny@test", "New Year", "20260101") };
        var (accounts, scheduler) = BuildServices(db, handler);

        Assert.Null(await scheduler.SyncNowAsync(Guid.NewGuid(), CancellationToken.None));

        var accountId = await accounts.ConnectIcsAsync(Feed, "Holidays", 60, null, CancellationToken.None);
        var summary = await scheduler.SyncNowAsync(accountId, CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.Calendars);

        var status = Assert.Single(await scheduler.GetStatusAsync(CancellationToken.None));
        Assert.Equal(accountId, status.AccountId);
        Assert.Equal(nameof(SyncRunState.Idle), status.State);
        Assert.NotNull(status.NextRunAtUtc);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static string SingleDay(string uid, string summary, string date) => $"""
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:{uid}
        SUMMARY:{summary}
        DTSTART;VALUE=DATE:{date}
        END:VEVENT
        END:VCALENDAR
        """;

    private static Task<SyncState> AccountStateAsync(CalendarDbContext db, Guid accountId) =>
        db.SyncStates.FirstAsync(s => s.AccountId == accountId && s.CalendarId == null);

    private static (IAccountService Accounts, ISyncScheduler Scheduler) BuildServices(
        CalendarDbContext db, HttpMessageHandler handler, SyncSchedulerOptions? options = null)
    {
        var registry = new PluginRegistry();
        var icsPlugin = new IcsPlugin();
        registry.Register(new PluginRegistration(
            icsPlugin.Manifest,
            new[] { Capability.CalendarRead },
            PluginState.Running,
            new TestInstance(icsPlugin.Manifest.Id, icsPlugin)));

        var device = new DeviceProvider(db);
        var vault = new SecretVault(db);
        var dedup = new DedupGrouper(db);
        var cache = new InMemoryPluginCache();
        var http = new HttpClient(handler);

        var geocodeAggregator = new Calendar.Infrastructure.Aggregation.GeocodeAggregator(
            registry,
            new Calendar.Infrastructure.Aggregation.InMemoryAggregationResultCache(),
            NullLogger<Calendar.Infrastructure.Aggregation.GeocodeAggregator>.Instance);
        var geocode = new GeocodeService(
            db, geocodeAggregator, registry, device, NullLogger<GeocodeService>.Instance);

        var hostFactory = new PluginHostServicesFactory(
            vault, new NoopAuthBrokerFactory(), cache, http, NullLoggerFactory.Instance);
        var sync = new CalendarSyncService(db, registry, hostFactory, dedup, device, geocode, NullLoggerFactory.Instance);
        var accounts = new AccountService(db, vault, sync, registry, device);
        var scheduler = new SyncScheduler(
            db, sync, device, Options.Create(options ?? new SyncSchedulerOptions()), NullLogger<SyncScheduler>.Instance);
        return (accounts, scheduler);
    }

    /// <summary>A url → body map the test mutates to make a feed appear, change, or start failing (404).</summary>
    private sealed class MutableMapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

        public string this[string url]
        {
            set => _map[url] = value;
        }

        public void Remove(string url) => _map.Remove(url);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_map.TryGetValue(request.RequestUri!.ToString(), out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/calendar"),
            });
        }
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
