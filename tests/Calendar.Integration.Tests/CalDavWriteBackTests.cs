using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Calendar.Application.Auth;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Auth;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.CalDav;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CalendarEntity = Calendar.Domain.Entities.Calendar;

namespace Calendar.Integration.Tests;

/// <summary>
/// Proves the write path drives the REAL CalDAV plugin through the shared host-services helper with the REAL
/// <see cref="AuthBrokerFactory"/>, so the AppPassword scheme actually applies a Basic header (ARCHITECTURE
/// §8; PLUGIN-HOST.md §6). No live network — a stub WebDAV server over <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class CalDavWriteBackTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"calendar-caldav-write-{Guid.NewGuid():N}.db");

    private CalendarDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    [Fact]
    public async Task Create_through_the_real_caldav_plugin_PUTs_with_a_Basic_header_and_stores_the_etag()
    {
        using var db = NewDb();
        db.Database.Migrate();

        var handler = new RecordingHandler();
        // PUT (If-None-Match:*) → 201 with an ETag the host should capture.
        handler.OnPut(HttpStatusCode.Created, etag: "\"caldav-etag-1\"");

        var (service, _) = await BuildAsync(db, handler);
        var calendar = await SeedCalDavCalendarAsync(db);

        var result = await service.CreateEventAsync(
            calendar.Id,
            new EventWriteRequest("Dentist",
                new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero),
                AllDay: false, Location: null, Rrule: null, Categories: null),
            CancellationToken.None);

        Assert.Equal(WriteDisposition.Applied, result.Disposition);

        var put = Assert.Single(handler.Puts);
        Assert.StartsWith("Basic ", put.Authorization);                  // the real broker applied AppPassword
        var expectedBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("me@test:app-pw"));
        Assert.Equal($"Basic {expectedBasic}", put.Authorization);
        Assert.Contains("BEGIN:VEVENT", put.Body);
        Assert.Contains("SUMMARY:Dentist", put.Body);

        var stored = await db.Events.SingleAsync(e => e.Id == result.EventId);
        Assert.Equal("\"caldav-etag-1\"", stored.ETag);
    }

    [Fact]
    public async Task A_412_from_caldav_on_update_replay_marks_the_outbox_Conflict()
    {
        using var db = NewDb();
        db.Database.Migrate();

        // First create succeeds (so we have a local event with an ETag), then offline-queue an update, then 412.
        var handler = new RecordingHandler();
        handler.OnPut(HttpStatusCode.Created, etag: "\"v1\"");

        var (service, _) = await BuildAsync(db, handler);
        var calendar = await SeedCalDavCalendarAsync(db);
        var created = await service.CreateEventAsync(calendar.Id, Req("V1"), CancellationToken.None);

        // Queue an update while the network is down.
        handler.FailNext = true;
        var queued = await service.UpdateEventAsync(created.EventId, Req("V2"), CancellationToken.None);
        Assert.Equal(WriteDisposition.Queued, queued.Disposition);

        // Provider back, but the resource diverged → 412 Precondition Failed.
        handler.FailNext = false;
        handler.OnPut(HttpStatusCode.PreconditionFailed, etag: null);

        var summary = await service.ReplayAsync(CancellationToken.None);

        Assert.Equal(1, summary.Conflicts);
        var row = await db.WriteOutbox.Where(w => w.Operation == OutboxOp.Update).SingleAsync();
        Assert.Equal(OutboxStatus.Conflict, row.Status);
    }

    private static EventWriteRequest Req(string title) =>
        new(title,
            new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero),
            AllDay: false, Location: null, Rrule: null, Categories: null);

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────

#pragma warning disable CS1998 // kept Task-returning for symmetry with the other harnesses
    private async Task<(WriteService Service, Guid SecretRef)> BuildAsync(CalendarDbContext db, HttpMessageHandler handler)
    {
        var registry = new PluginRegistry();
        var plugin = new CalDavPlugin();
        registry.Register(new PluginRegistration(
            plugin.Manifest,
            new[] { Capability.CalendarRead, Capability.CalendarWrite },
            PluginState.Running,
            new TestInstance(CalDavPlugin.Id, plugin)));

        var vault = new SecretVault(db);
        var cache = new InMemoryPluginCache();
        var http = new HttpClient(handler);

        // The REAL broker factory with an in-memory token vault: AppPassword reads the vaulted password under
        // the per-account AEAD and applies a Basic header — exactly the production path.
        var tokenVault = new InMemoryTokenVault();
        var authFactory = new AuthBrokerFactory(
            tokenVault, () => http, NullLoggerFactory.Instance, TimeProvider.System);

        var hostFactory = new PluginHostServicesFactory(vault, authFactory, cache, http, NullLoggerFactory.Instance);
        var service = new WriteService(db, registry, hostFactory, NullLogger<WriteService>.Instance);

        _tokenVault = tokenVault;
        return (service, Guid.Empty);
    }
#pragma warning restore CS1998

    private InMemoryTokenVault _tokenVault = default!;

    private async Task<CalendarEntity> SeedCalDavCalendarAsync(CalendarDbContext db)
    {
        if (!await db.Plugins.AnyAsync(p => p.Id == CalDavPlugin.Id))
        {
            db.Plugins.Add(new Calendar.Domain.Entities.Plugin
            {
                Id = CalDavPlugin.Id, Name = "CalDAV", Version = "1.0.0", SdkVersion = "1.x",
                Kind = PluginKind.Assembly, Status = PluginStatus.Installed, TrustTier = TrustTier.InBox,
                Capabilities = new List<string> { "calendar.read", "calendar.write" },
                Manifest = "{}", InstalledAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var accountId = Guid.CreateVersion7();

        // Stash the app-password in the token vault under the account AEAD (what the real broker reads).
        var secretRef = await _tokenVault.StoreAsync(
            SecretKind.AppPassword, "app-pw", aad: $"account:{accountId:N}", expiresAtUtc: null, CancellationToken.None);

        // The account's config JSON carries serverUrl + username (for GetConfig) and the secretRef/username the
        // host-services helper lifts into the CredentialContext.
        var configJson = JsonSerializer.Serialize(new
        {
            serverUrl = "https://dav.test/",
            username = "me@test",
            secretRef = secretRef.ToString(),
        });
        var authRef = await new SecretVault(db).StoreAsync(SecretKind.FeedUrl, configJson, CancellationToken.None);

        db.Accounts.Add(new Account
        {
            Id = accountId, PluginId = CalDavPlugin.Id, DisplayName = "iCloud", AuthRef = authRef,
            Status = AccountStatus.Connected, UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = Guid.NewGuid(), Lamport = 1,
        });

        var calendar = new CalendarEntity
        {
            Id = Guid.CreateVersion7(),
            AccountId = accountId,
            RemoteId = "https://dav.test/cal/home/",
            Name = "Personal",
            IsVisible = true,
            IsReadOnly = false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = Guid.NewGuid(),
            Lamport = 1,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync();
        return calendar;
    }

    private sealed record TestInstance(string PluginId, IPlugin? Plugin) : IPluginInstance;

    /// <summary>Records PUT/DELETE; PUT bodies are the iCalendar the plugin serialized. Can simulate offline.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _putStatus = HttpStatusCode.Created;
        private string? _putEtag = "\"etag\"";
        public bool FailNext { get; set; }
        public List<(string Body, string? Authorization)> Puts { get; } = new();

        public void OnPut(HttpStatusCode status, string? etag)
        {
            _putStatus = status;
            _putEtag = etag;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (FailNext)
                throw new HttpRequestException("connection refused");

            if (request.Method == HttpMethod.Put)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                Puts.Add((body, request.Headers.Authorization?.ToString()));
                var resp = new HttpResponseMessage(_putStatus);
                if (_putEtag is not null)
                    resp.Headers.TryAddWithoutValidation("ETag", _putEtag);
                return resp;
            }

            // Any other verb (a follow-up PROPFIND for the ETag when the PUT omits it) — not needed here.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>A process-local <see cref="ITokenVault"/>: AAD must match on read (mirrors the AEAD binding).</summary>
    private sealed class InMemoryTokenVault : ITokenVault
    {
        private readonly ConcurrentDictionary<Guid, (SecretKind Kind, string Value, string? Aad)> _store = new();

        public Task<Guid> StoreAsync(SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            var id = Guid.CreateVersion7();
            _store[id] = (kind, value, aad);
            return Task.FromResult(id);
        }

        public Task UpdateAsync(Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
        {
            _store[id] = (kind, value, aad);
            return Task.CompletedTask;
        }

        public Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct)
        {
            if (_store.TryGetValue(id, out var entry) && entry.Aad == aad)
                return Task.FromResult<VaultEntry?>(new VaultEntry(entry.Kind, entry.Value, null));
            return Task.FromResult<VaultEntry?>(null);
        }

        public Task RemoveAsync(Guid id, CancellationToken ct)
        {
            _store.TryRemove(id, out _);
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
