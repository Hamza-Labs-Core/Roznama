using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Calendar.Plugin.Abstractions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace Calendar.Plugin.CalDav;

/// <summary>
/// The CalDAV plugin (caldav-plugin.md): one implementation that serves iCloud, Fastmail, Nextcloud, and any
/// standards-compliant server. It speaks WebDAV (<c>PROPFIND</c>/<c>REPORT</c>) + CalDAV
/// (<c>calendar-query</c>/<c>calendar-multiget</c>/<c>sync-collection</c>) and parses iCalendar bodies with
/// Ical.Net. Discovery → enumerate → fetch (initial) → sync (delta) → parse → normalize, plus optimistic
/// write-back via <c>PUT</c>/<c>DELETE</c> with <c>If-Match</c>.
/// </summary>
public sealed class CalDavPlugin : ICalendarSource, ICalendarWriter
{
    public const string Id = "org.unifiedcalendar.caldav";

    private static readonly HttpMethod Propfind = new("PROPFIND");
    private static readonly HttpMethod Report = new("REPORT");
    private const string XmlContentType = "application/xml";

    private IPluginHost _host = default!;
    private CalDavConfig _config = default!;

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "CalDAV",
        Version: "1.0.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.CalendarRead, CapabilityIds.CalendarWrite },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(AuthScheme.AppPassword),
        Network: new NetworkSpec(new[]
        {
            "caldav.icloud.com", "*.caldav.icloud.com", "caldav.fastmail.com", "*"
        }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"serverUrl":{"type":"string"},"username":{"type":"string"}},"required":["serverUrl","username"]}""",
            new[] { "serverUrl", "username" }));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        _config = host.GetConfig<CalDavConfig>();
        return Task.CompletedTask;
    }

    // ── calendar.read ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var home = await DiscoverHomeSetAsync(client, ct).ConfigureAwait(false);

        var xml = await PropfindAsync(client, home, depth: "1", WebDav.CalendarCollectionsBody(), ct)
            .ConfigureAwait(false);
        var collections = WebDav.ParseCalendarCollections(xml);

        var calendars = new List<RemoteCalendar>(collections.Count);
        foreach (var c in collections)
            calendars.Add(new RemoteCalendar(ResolveHref(home, c.Href), c.Name, c.Color, c.IsReadOnly));
        return calendars;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var calendarUrl = AbsoluteUrl(remoteCalendarId);

        var (kind, value) = ParseToken(syncToken);

        // Prefer RFC 6578 sync-collection; on a server that doesn't support it, fall through to CTag/ETag-diff.
        if (kind != TokenKind.Ctag)
        {
            var report = await TrySyncCollectionAsync(client, calendarUrl, value, ct).ConfigureAwait(false);
            if (report is { } r)
                return await BuildDeltaFromSyncCollectionAsync(client, calendarUrl, r, ct).ConfigureAwait(false);
        }

        // CTag / ETag-diff fallback (caldav-plugin.md §7).
        return await SyncByCTagAsync(client, calendarUrl, syncToken, ct).ConfigureAwait(false);
    }

    private async Task<SyncCollectionResult?> TrySyncCollectionAsync(
        HttpClient client, string calendarUrl, string? token, CancellationToken ct)
    {
        using var request = NewRequest(Report, calendarUrl, WebDav.SyncCollectionBody(token));
        request.Headers.TryAddWithoutValidation("Depth", "1");
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // An invalidated sync-token: the server rejects it with 409/410 → force a full resync.
        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.Conflict)
            throw new SyncResetRequiredException(
                $"CalDAV sync-token was rejected ({(int)response.StatusCode}); a full resync is required.");

        // sync-collection unsupported (405/501/400) → signal the caller to use the CTag fallback.
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented
            or HttpStatusCode.BadRequest
            or HttpStatusCode.Forbidden)
            return null;

        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return WebDav.ParseSyncCollection(xml);
    }

    private async Task<SyncResult> BuildDeltaFromSyncCollectionAsync(
        HttpClient client, string calendarUrl, SyncCollectionResult report, CancellationToken ct)
    {
        var upserts = new List<RemoteEvent>();
        var deletes = new List<string>();

        var changedHrefs = report.Changed
            .Select(r => AbsoluteUrl(ResolveHref(calendarUrl, r.Href)))
            .ToList();

        if (changedHrefs.Count > 0)
        {
            var bodies = await MultiGetAsync(client, calendarUrl, changedHrefs, ct).ConfigureAwait(false);
            foreach (var resource in bodies)
                AppendResource(resource, calendarUrl, upserts, deletes);
        }

        foreach (var removed in report.Removed)
            deletes.Add(ResolveHref(calendarUrl, removed));

        return new SyncResult(upserts, deletes, MintToken(TokenKind.Sync, report.NewSyncToken));
    }

    private async Task<SyncResult> SyncByCTagAsync(
        HttpClient client, string calendarUrl, string? priorToken, CancellationToken ct)
    {
        // A calendar-query with calendar-data is the simplest correct full read; we then diff stored ETags to
        // synthesize upserts/deletes the same way the sync-collection path does (caldav-plugin.md §6/§7).
        var xml = await ReportAsync(client, calendarUrl, depth: "1", WebDav.CalendarQueryBody(), ct)
            .ConfigureAwait(false);
        var resources = WebDav.ParseResources(xml);

        var (priorKind, priorCtag) = ParseToken(priorToken);
        // Re-read the collection CTag to mint the next token (cheap, single PROPFIND on the collection).
        var newCtag = await ReadCTagAsync(client, calendarUrl, ct).ConfigureAwait(false);

        var prior = await LoadSnapshotAsync(calendarUrl, ct).ConfigureAwait(false);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var upserts = new List<RemoteEvent>();
        var deletes = new List<string>();

        foreach (var resource in resources)
        {
            var resolvedHref = ResolveHref(calendarUrl, resource.Href);
            if (resource.CalendarData is null)
                continue;

            var events = CalDavNormalizer.Parse(resource.CalendarData, resolvedHref, resource.ETag);
            foreach (var ev in events)
            {
                if (ev.Status == EventStatus.Cancelled)
                {
                    deletes.Add(ev.RemoteId);
                    continue;
                }
                var tag = ev.ChangeTag ?? string.Empty;
                next[ev.RemoteId] = tag;
                if (!prior.TryGetValue(ev.RemoteId, out var priorTag) || priorTag != tag)
                    upserts.Add(ev);
            }
        }

        // Anything in the prior snapshot but gone from this read was deleted on the server.
        foreach (var remoteId in prior.Keys)
            if (!next.ContainsKey(remoteId))
                deletes.Add(remoteId);

        await SaveSnapshotAsync(calendarUrl, next, ct).ConfigureAwait(false);
        return new SyncResult(upserts, deletes, MintToken(TokenKind.Ctag, newCtag ?? priorCtag ?? string.Empty));
    }

    private async Task<IReadOnlyList<CalDavResource>> MultiGetAsync(
        HttpClient client, string calendarUrl, IReadOnlyList<string> hrefs, CancellationToken ct)
    {
        if (hrefs.Count == 0)
            return Array.Empty<CalDavResource>();
        var xml = await ReportAsync(client, calendarUrl, depth: "1", WebDav.MultiGetBody(hrefs), ct)
            .ConfigureAwait(false);
        return WebDav.ParseResources(xml);
    }

    private void AppendResource(
        CalDavResource resource, string calendarUrl, List<RemoteEvent> upserts, List<string> deletes)
    {
        var resolvedHref = ResolveHref(calendarUrl, resource.Href);
        if (resource.CalendarData is null)
        {
            // A multiget row with no body (404 within the 207) → the resource is gone.
            deletes.Add(resolvedHref);
            return;
        }

        foreach (var ev in CalDavNormalizer.Parse(resource.CalendarData, resolvedHref, resource.ETag))
        {
            if (ev.Status == EventStatus.Cancelled)
                deletes.Add(ev.RemoteId);
            else
                upserts.Add(ev);
        }
    }

    // ── calendar.write ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<RemoteEvent> CreateEventAsync(string remoteCalendarId, RemoteEvent draft, CancellationToken ct)
    {
        var client = _host.CreateClient();
        var calendarUrl = AbsoluteUrl(remoteCalendarId);

        var uid = string.IsNullOrWhiteSpace(draft.Uid) ? Guid.NewGuid().ToString("N") : draft.Uid;
        var resourceUrl = CombineUrl(calendarUrl, $"{uid}.ics");
        var ics = SerializeEvent(draft, uid);

        using var request = new HttpRequestMessage(HttpMethod.Put, resourceUrl)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
        };
        // If-None-Match: * → fail if the resource already exists (caldav-plugin.md §9).
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new ConcurrencyConflictException(
                $"A resource already exists at {resourceUrl} (If-None-Match:* failed).");
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var etag = await ResolveETagAsync(client, response, resourceUrl, ct).ConfigureAwait(false);
        return draft with { RemoteId = resourceUrl, Uid = uid, ChangeTag = etag };
    }

    /// <inheritdoc />
    public async Task<RemoteEvent> UpdateEventAsync(
        string remoteCalendarId, RemoteEvent updated, string? ifMatchChangeTag, CancellationToken ct)
    {
        var client = _host.CreateClient();
        var resourceUrl = AbsoluteUrl(updated.RemoteId);
        var uid = string.IsNullOrWhiteSpace(updated.Uid) ? Guid.NewGuid().ToString("N") : updated.Uid;
        var ics = SerializeEvent(updated, uid);

        using var request = new HttpRequestMessage(HttpMethod.Put, resourceUrl)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
        };
        if (!string.IsNullOrEmpty(ifMatchChangeTag))
            request.Headers.TryAddWithoutValidation("If-Match", ifMatchChangeTag);
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new ConcurrencyConflictException(
                $"If-Match precondition failed updating {resourceUrl}; re-fetch and merge.");
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var etag = await ResolveETagAsync(client, response, resourceUrl, ct).ConfigureAwait(false);
        return updated with { RemoteId = resourceUrl, Uid = uid, ChangeTag = etag };
    }

    /// <inheritdoc />
    public async Task DeleteEventAsync(
        string remoteCalendarId, string remoteEventId, string? ifMatchChangeTag, CancellationToken ct)
    {
        var client = _host.CreateClient();
        var resourceUrl = AbsoluteUrl(remoteEventId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, resourceUrl);
        if (!string.IsNullOrEmpty(ifMatchChangeTag))
            request.Headers.TryAddWithoutValidation("If-Match", ifMatchChangeTag);
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new ConcurrencyConflictException(
                $"If-Match precondition failed deleting {resourceUrl}; re-fetch and merge.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return; // already gone — idempotent delete
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ── Discovery ───────────────────────────────────────────────────────────────────────────────────

    private async Task<string> DiscoverHomeSetAsync(HttpClient client, CancellationToken ct)
    {
        var seed = WellKnown(_config.ServerUrl);

        // 1. current-user-principal.
        var principalXml = await PropfindAsync(client, seed, depth: "0", WebDav.CurrentUserPrincipalBody(), ct)
            .ConfigureAwait(false);
        var principalHref = WebDav.ParseCurrentUserPrincipal(principalXml)
            ?? throw new InvalidOperationException("CalDAV discovery failed: no current-user-principal returned.");
        var principalUrl = ResolveHref(seed, principalHref);

        // 2. calendar-home-set.
        var homeXml = await PropfindAsync(client, principalUrl, depth: "0", WebDav.CalendarHomeSetBody(), ct)
            .ConfigureAwait(false);
        var homeHref = WebDav.ParseCalendarHomeSet(homeXml)
            ?? throw new InvalidOperationException("CalDAV discovery failed: no calendar-home-set returned.");
        return ResolveHref(principalUrl, homeHref);
    }

    private async Task<string?> ReadCTagAsync(HttpClient client, string calendarUrl, CancellationToken ct)
    {
        var xml = await PropfindAsync(client, calendarUrl, depth: "0", WebDav.CalendarCollectionsBody(), ct)
            .ConfigureAwait(false);
        var collections = WebDav.ParseCalendarCollections(xml);
        // Depth:0 on the collection may or may not echo resourcetype=calendar; fall back to any returned CTag.
        if (collections.Count > 0)
            return collections[0].CTag;
        return null;
    }

    // ── HTTP primitives ─────────────────────────────────────────────────────────────────────────────

    private async Task<string> PropfindAsync(
        HttpClient client, string url, string depth, string body, CancellationToken ct)
    {
        using var request = NewRequest(Propfind, url, body);
        request.Headers.TryAddWithoutValidation("Depth", depth);
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> ReportAsync(
        HttpClient client, string url, string depth, string body, CancellationToken ct)
    {
        using var request = NewRequest(Report, url, body);
        request.Headers.TryAddWithoutValidation("Depth", depth);
        await _host.Auth.ApplyAsync(request, null, ct).ConfigureAwait(false);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string body) =>
        new(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, XmlContentType),
        };

    /// <summary>401 → surface the iCloud app-specific-password guidance (caldav-plugin.md §13).</summary>
    private static Task EnsureAuthOkAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(
                "CalDAV authentication failed (401). Use an app-specific password — the normal account " +
                "password fails when two-factor auth is enabled (iCloud/Fastmail/Nextcloud).");
        return Task.CompletedTask;
    }

    /// <summary>Read the ETag a write returned; fall back to a HEAD/PROPFIND-less GET only if the server omits it.</summary>
    private async Task<string?> ResolveETagAsync(
        HttpClient client, HttpResponseMessage writeResponse, string resourceUrl, CancellationToken ct)
    {
        var etag = writeResponse.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(etag))
            return etag;

        // Some servers don't echo the ETag on PUT; a follow-up PROPFIND Depth:0 fetches getetag.
        try
        {
            var xml = await PropfindAsync(client, resourceUrl, depth: "0",
                """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:getetag/></d:prop></d:propfind>""",
                ct).ConfigureAwait(false);
            return WebDav.ParseResources(xml).FirstOrDefault()?.ETag;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ── Write serialization ─────────────────────────────────────────────────────────────────────────

    /// <summary>Serialize a <see cref="RemoteEvent"/> draft to an iCalendar VEVENT body for PUT (caldav-plugin.md §9).</summary>
    internal static string SerializeEvent(RemoteEvent ev, string uid)
    {
        var calendar = new Ical.Net.Calendar();
        var vevent = new CalendarEvent
        {
            Uid = uid,
            Summary = ev.Title,
        };

        if (ev.AllDay)
        {
            vevent.Start = new CalDateTime(ev.StartUtc.UtcDateTime.Date) { HasTime = false };
            // RFC 5545 all-day DTEND is exclusive; our stored end is inclusive, so add a day.
            vevent.End = new CalDateTime(ev.EndUtc.UtcDateTime.Date.AddDays(1)) { HasTime = false };
        }
        else
        {
            vevent.Start = new CalDateTime(ev.StartUtc.UtcDateTime, "UTC");
            vevent.End = new CalDateTime(ev.EndUtc.UtcDateTime, "UTC");
        }

        if (!string.IsNullOrWhiteSpace(ev.Location))
            vevent.Location = ev.Location;
        if (ev.Geo is { } g)
            vevent.GeographicLocation = new GeographicLocation(g.Lat, g.Lng);
        foreach (var category in ev.Categories)
            vevent.Categories.Add(category);
        if (!string.IsNullOrWhiteSpace(ev.Rrule))
            vevent.RecurrenceRules = new List<RecurrencePattern> { new(ev.Rrule) };

        vevent.Status = ev.Status switch
        {
            EventStatus.Cancelled => "CANCELLED",
            EventStatus.Tentative => "TENTATIVE",
            _ => "CONFIRMED",
        };

        calendar.Events.Add(vevent);
        return new CalendarSerializer().SerializeToString(calendar);
    }

    // ── URL & token helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>RFC 6764: probe <c>/.well-known/caldav</c> from the seed host so paths aren't hardcoded.</summary>
    internal static string WellKnown(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return serverUrl;
        // If the seed already targets a specific path (e.g. Fastmail /dav/), keep it; only bare-host seeds get
        // the well-known probe.
        if (uri.AbsolutePath is "/" or "")
            return new Uri(uri, "/.well-known/caldav").ToString();
        return serverUrl;
    }

    /// <summary>Resolve a possibly-relative href against the response/base URL (caldav-plugin.md §4, §10).</summary>
    internal static string ResolveHref(string baseUrl, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) &&
            Uri.TryCreate(b, href, out var resolved))
            return resolved.ToString();
        return href;
    }

    private string AbsoluteUrl(string urlOrHref) => ResolveHref(WellKnown(_config.ServerUrl), urlOrHref);

    private static string CombineUrl(string calendarUrl, string fileName)
    {
        var trimmed = calendarUrl.EndsWith('/') ? calendarUrl : calendarUrl + "/";
        return trimmed + fileName;
    }

    // ── Sync token (kind-tagged so the read path knows whether to use sync-collection or CTag) ─────────

    private enum TokenKind { Sync, Ctag }

    private static string MintToken(TokenKind kind, string value) =>
        $"{(kind == TokenKind.Sync ? "sc" : "ct")}:{value}";

    private static (TokenKind Kind, string? Value) ParseToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return (TokenKind.Sync, null);
        if (token.StartsWith("ct:", StringComparison.Ordinal))
            return (TokenKind.Ctag, token["ct:".Length..]);
        if (token.StartsWith("sc:", StringComparison.Ordinal))
            return (TokenKind.Sync, token["sc:".Length..]);
        // Unprefixed legacy/raw token — treat as a sync-collection token.
        return (TokenKind.Sync, token);
    }

    // ── Snapshot cache (for the CTag/ETag-diff fallback delta) ────────────────────────────────────────

    private async Task<Dictionary<string, string>> LoadSnapshotAsync(string calendarUrl, CancellationToken ct)
    {
        var bytes = await _host.Cache.GetAsync(SnapshotKey(calendarUrl), ct).ConfigureAwait(false);
        if (bytes is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private Task SaveSnapshotAsync(string calendarUrl, Dictionary<string, string> snapshot, CancellationToken ct) =>
        _host.Cache.SetAsync(SnapshotKey(calendarUrl), JsonSerializer.SerializeToUtf8Bytes(snapshot), ttl: null, ct);

    private static string SnapshotKey(string calendarUrl) => $"caldav-snapshot:{calendarUrl}";
}
