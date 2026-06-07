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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 6 write-back (ARCHITECTURE §8/§10; SDK-CONTRACT §4.write): create/update/delete round-trip through a
/// <c>calendar.write</c> plugin; read-only calendars reject writes; an unreachable provider enqueues to
/// <c>WriteOutbox</c> and a later <see cref="IWriteService.ReplayAsync"/> drains it; a 412 marks Conflict.
/// No live network — a stub <see cref="ICalendarWriter"/> + temp SQLite.
/// </summary>
public sealed class WriteServiceTests : IDisposable
{
    private const string PluginId = "test.writer";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-write-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Create_round_trips_through_the_writer_and_stores_the_returned_etag()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);

        var result = await service.CreateEventAsync(calendar.Id, NewRequest("Standup"), CancellationToken.None);

        Assert.Equal(WriteDisposition.Applied, result.Disposition);
        Assert.Null(result.OutboxId);
        Assert.Single(writer.Created);

        var stored = await db.Events.SingleAsync(e => e.Id == result.EventId);
        Assert.Equal("server-remote-1", stored.RemoteId);     // provider-assigned id replaces the provisional one
        Assert.Equal("\"etag-1\"", stored.ETag);
        Assert.Equal("Standup", stored.Title);
        Assert.Empty(await db.WriteOutbox.ToListAsync());
    }

    [Fact]
    public async Task Update_round_trips_and_sends_if_match_with_the_prior_etag()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);
        var created = await service.CreateEventAsync(calendar.Id, NewRequest("V1"), CancellationToken.None);

        var result = await service.UpdateEventAsync(created.EventId, NewRequest("V2"), CancellationToken.None);

        Assert.Equal(WriteDisposition.Applied, result.Disposition);
        var (_, ifMatch) = Assert.Single(writer.Updated);
        Assert.Equal("\"etag-1\"", ifMatch);                  // the prior ETag travels as If-Match
        var stored = await db.Events.SingleAsync(e => e.Id == created.EventId);
        Assert.Equal("V2", stored.Title);
        Assert.Equal("\"etag-2\"", stored.ETag);              // refreshed from the writer's response
    }

    [Fact]
    public async Task Delete_round_trips_and_removes_the_local_event()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);
        var created = await service.CreateEventAsync(calendar.Id, NewRequest("Gone"), CancellationToken.None);

        var result = await service.DeleteEventAsync(created.EventId, CancellationToken.None);

        Assert.Equal(WriteDisposition.Applied, result.Disposition);
        Assert.Single(writer.Deleted);
        Assert.False(await db.Events.AnyAsync(e => e.Id == created.EventId));
    }

    [Fact]
    public async Task A_write_to_a_read_only_calendar_is_rejected()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedCalendarAsync(db, isReadOnly: true);

        await Assert.ThrowsAsync<ReadOnlyCalendarException>(() =>
            service.CreateEventAsync(calendar.Id, NewRequest("Nope"), CancellationToken.None));

        Assert.Empty(writer.Created);
        Assert.Empty(await db.Events.ToListAsync());          // nothing reflected locally either
    }

    [Fact]
    public async Task An_unreachable_provider_enqueues_to_the_outbox_and_a_later_replay_drains_it()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);

        writer.FailNextWith = new HttpRequestException("connection refused");
        var queued = await service.CreateEventAsync(calendar.Id, NewRequest("Offline"), CancellationToken.None);

        Assert.Equal(WriteDisposition.Queued, queued.Disposition);
        Assert.NotNull(queued.OutboxId);
        var row = await db.WriteOutbox.SingleAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(OutboxOp.Create, row.Operation);
        Assert.Empty(writer.Created);                         // nothing reached the provider yet
        // The local event is reflected optimistically so the UI shows it.
        Assert.True(await db.Events.AnyAsync(e => e.Id == queued.EventId));

        // Provider reachable again → replay drains FIFO.
        writer.FailNextWith = null;
        var summary = await service.ReplayAsync(CancellationToken.None);

        Assert.Equal(1, summary.Drained);
        Assert.Equal(0, summary.Conflicts);
        Assert.Single(writer.Created);
        Assert.Equal(OutboxStatus.Done, (await db.WriteOutbox.SingleAsync()).Status);
        var stored = await db.Events.SingleAsync(e => e.Id == queued.EventId);
        Assert.Equal("server-remote-1", stored.RemoteId);     // the replay reconciled the provider id
    }

    [Fact]
    public async Task A_412_on_replay_marks_the_outbox_row_Conflict()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);
        var created = await service.CreateEventAsync(calendar.Id, NewRequest("V1"), CancellationToken.None);

        // Queue an update while offline, then make the provider answer 412 on replay.
        writer.FailNextWith = new HttpRequestException("offline");
        var queued = await service.UpdateEventAsync(created.EventId, NewRequest("V2"), CancellationToken.None);
        Assert.Equal(WriteDisposition.Queued, queued.Disposition);

        writer.FailNextWith = null;
        writer.ConflictOnUpdate = true;
        var summary = await service.ReplayAsync(CancellationToken.None);

        Assert.Equal(0, summary.Drained);
        Assert.Equal(1, summary.Conflicts);
        var row = await db.WriteOutbox.Where(w => w.Operation == OutboxOp.Update).SingleAsync();
        Assert.Equal(OutboxStatus.Conflict, row.Status);
        Assert.NotNull(row.LastError);
    }

    [Fact]
    public async Task Outbox_status_reports_queued_counts()
    {
        using var db = NewDb();
        db.Database.Migrate();
        var (writer, service) = Build(db);
        var calendar = await SeedWritableCalendarAsync(db);

        writer.FailNextWith = new HttpRequestException("offline");
        await service.CreateEventAsync(calendar.Id, NewRequest("A"), CancellationToken.None);
        writer.FailNextWith = new HttpRequestException("offline");
        await service.CreateEventAsync(calendar.Id, NewRequest("B"), CancellationToken.None);

        var status = await service.GetOutboxStatusAsync(CancellationToken.None);
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Pending);
        Assert.Equal(2, status.Items.Count);
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────

    private static (StubWriter Writer, WriteService Service) Build(CalendarDbContext db)
    {
        var registry = new PluginRegistry();
        var writer = new StubWriter();
        registry.Register(new PluginRegistration(
            writer.Manifest,
            new[] { Capability.CalendarRead, Capability.CalendarWrite },
            PluginState.Running,
            new TestInstance(PluginId, writer)));

        var vault = new SecretVault(db);
        var cache = new InMemoryPluginCache();
        var http = new HttpClient(new ThrowingHandler());
        var hostFactory = new PluginHostServicesFactory(
            vault, new NoopAuthBrokerFactory(), cache, http, NullLoggerFactory.Instance);

        var service = new WriteService(db, registry, hostFactory, NullLogger<WriteService>.Instance);
        return (writer, service);
    }

    private async Task<CalendarEntity> SeedWritableCalendarAsync(CalendarDbContext db) =>
        await SeedCalendarAsync(db, isReadOnly: false);

    private static async Task<CalendarEntity> SeedCalendarAsync(CalendarDbContext db, bool isReadOnly)
    {
        if (!await db.Plugins.AnyAsync(p => p.Id == PluginId))
        {
            db.Plugins.Add(new Calendar.Domain.Entities.Plugin
            {
                Id = PluginId,
                Name = "Test Writer",
                Version = "1.0.0",
                SdkVersion = "1.x",
                Kind = PluginKind.Assembly,
                Status = PluginStatus.Installed,
                TrustTier = TrustTier.InBox,
                Capabilities = new List<string> { "calendar.read", "calendar.write" },
                Manifest = "{}",
                InstalledAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var account = new Account
        {
            Id = Guid.CreateVersion7(),
            PluginId = PluginId,
            DisplayName = "Test",
            Status = AccountStatus.Connected,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = Guid.NewGuid(),
            Lamport = 1,
        };
        db.Accounts.Add(account);

        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(),
            AccountId = account.Id,
            RemoteId = "https://dav.test/cal/",
            Name = isReadOnly ? "Holidays" : "Personal",
            IsVisible = true,
            IsReadOnly = isReadOnly,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = account.DeviceId,
            Lamport = 1,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    private static EventWriteRequest NewRequest(string title) =>
        new(title,
            new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero),
            AllDay: false, Location: null, Rrule: null, Categories: null);

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    /// <summary>A guard handler: the write path must never actually hit the network through the stub writer.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("the stub writer must not perform real HTTP");
    }

    /// <summary>
    /// A controllable <c>calendar.write</c> plugin. <see cref="FailNextWith"/> simulates an unreachable provider
    /// (the host then queues offline); <see cref="ConflictOnUpdate"/> simulates a 412 → ConcurrencyConflict.
    /// </summary>
    private sealed class StubWriter : ICalendarSource, ICalendarWriter
    {
        public List<RemoteEvent> Created { get; } = new();
        public List<(RemoteEvent Event, string? IfMatch)> Updated { get; } = new();
        public List<(string RemoteId, string? IfMatch)> Deleted { get; } = new();

        public Exception? FailNextWith { get; set; }
        public bool ConflictOnUpdate { get; set; }

        public PluginManifest Manifest { get; } = new(
            Id: PluginId,
            Name: "Test Writer",
            Version: "1.0.0",
            SdkVersion: "1.x",
            Kind: PluginKind.Assembly,
            Capabilities: new[] { CapabilityIds.CalendarRead, CapabilityIds.CalendarWrite },
            Publisher: new PluginPublisher("Test", Signature: null),
            Auth: new AuthSpec(AuthScheme.None),
            Network: new NetworkSpec(new[] { "*" }),
            Config: new ConfigSchema("""{"type":"object"}""", Array.Empty<string>()));

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RemoteCalendar>>(Array.Empty<RemoteCalendar>());

        public Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct) =>
            Task.FromResult(new SyncResult(Array.Empty<RemoteEvent>(), Array.Empty<string>(), "t"));

        public Task<RemoteEvent> CreateEventAsync(string remoteCalendarId, RemoteEvent draft, CancellationToken ct)
        {
            ThrowIfFailing();
            Created.Add(draft);
            return Task.FromResult(draft with { RemoteId = "server-remote-1", ChangeTag = "\"etag-1\"" });
        }

        public Task<RemoteEvent> UpdateEventAsync(
            string remoteCalendarId, RemoteEvent updated, string? ifMatchChangeTag, CancellationToken ct)
        {
            ThrowIfFailing();
            if (ConflictOnUpdate)
                throw new ConcurrencyConflictException("If-Match precondition failed (412).");
            Updated.Add((updated, ifMatchChangeTag));
            return Task.FromResult(updated with { ChangeTag = "\"etag-2\"" });
        }

        public Task DeleteEventAsync(
            string remoteCalendarId, string remoteEventId, string? ifMatchChangeTag, CancellationToken ct)
        {
            ThrowIfFailing();
            Deleted.Add((remoteEventId, ifMatchChangeTag));
            return Task.CompletedTask;
        }

        private void ThrowIfFailing()
        {
            if (FailNextWith is { } ex)
            {
                FailNextWith = null;
                throw ex;
            }
        }
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
