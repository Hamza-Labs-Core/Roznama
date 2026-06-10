using System.Net;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Google;

/// <summary>
/// The Google Calendar plugin (google-calendar-plugin.md): an OAuth <c>calendar.read</c> source over Google's
/// REST API. OAuth (host broker) → enumerate (<c>CalendarList.list</c>) → full fetch (<c>Events.list</c> +
/// <c>timeMin</c>/<c>timeMax</c>, <c>singleEvents=false</c>) → delta (<c>nextSyncToken</c>) → normalize. A
/// <c>410 GONE</c> on an incremental request throws <see cref="SyncResetRequiredException"/> so the host wipes
/// the token and re-runs a full sync.
///
/// The plugin never sees a token: it builds each request and calls <c>IPluginHost.Auth.ApplyAsync</c> for a
/// fresh bearer (it never instantiates an OAuth client or holds secrets — SDK-CONTRACT.md §3). It talks raw
/// REST/JSON over the host's egress-filtered <c>HttpClient</c> so the whole path is stub-testable.
/// </summary>
public sealed class GooglePlugin : ICalendarSource
{
    public const string Id = "org.unifiedcalendar.google";

    /// <summary>The least-privilege read scope requested from the broker (google §3).</summary>
    private static readonly IReadOnlyList<string> ReadScopes =
        new[] { "https://www.googleapis.com/auth/calendar.readonly" };

    private const string ApiBase = "https://www.googleapis.com/calendar/v3";
    private const int MaxResults = 2500;

    private IPluginHost _host = default!;
    private GoogleConfig _config = default!;

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "Google Calendar",
        Version: "0.1.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.CalendarRead },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(
            Scheme: AuthScheme.OAuth2Pkce,
            AuthorizationUrl: "https://accounts.google.com/o/oauth2/v2/auth",
            TokenUrl: "https://oauth2.googleapis.com/token",
            Scopes: ReadScopes,
            Params: new Dictionary<string, string>
            {
                ["access_type"] = "offline",
                ["prompt"] = "consent",
            }),
        Network: new NetworkSpec(new[]
        {
            "www.googleapis.com", "oauth2.googleapis.com", "accounts.google.com"
        }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"syncWindowMonthsPast":{"type":"integer","default":12},"syncWindowMonthsFuture":{"type":"integer","default":24}},"required":[]}""",
            Array.Empty<string>()));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        _config = host.GetConfig<GoogleConfig>();
        return Task.CompletedTask;
    }

    // ── calendar.read ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var calendars = new List<RemoteCalendar>();

        string? pageToken = null;
        do
        {
            var query = new List<string> { "maxResults=250", "showHidden=true" };
            if (pageToken is not null)
                query.Add($"pageToken={Uri.EscapeDataString(pageToken)}");

            var url = $"{ApiBase}/users/me/calendarList?{string.Join("&", query)}";
            using var doc = await GetJsonAsync(client, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    calendars.Add(GoogleNormalizer.NormalizeCalendar(item));

            pageToken = GetString(root, "nextPageToken");
        }
        while (pageToken is not null);

        return calendars;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose

        var upserts = new List<RemoteEvent>();
        var deletes = new List<string>();
        string? newSyncToken = null;
        string? pageToken = null;

        do
        {
            var url = BuildEventsUrl(remoteCalendarId, syncToken, pageToken);
            using var doc = await GetEventsPageAsync(client, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var ev = GoogleNormalizer.NormalizeEvent(item);
                    if (ev.Status == EventStatus.Cancelled)
                        deletes.Add(ev.RemoteId); // tombstone (incremental results always include these)
                    else
                        upserts.Add(ev);
                }
            }

            pageToken = GetString(root, "nextPageToken");
            // nextSyncToken appears ONLY on the final page; an intermediate page carries only nextPageToken.
            newSyncToken = GetString(root, "nextSyncToken") ?? newSyncToken;
        }
        while (pageToken is not null);

        // The token always arrives on the terminal page; preserve the prior one defensively if Google omitted it.
        return new SyncResult(upserts, deletes, newSyncToken ?? syncToken ?? string.Empty);
    }

    // ── Request building ──────────────────────────────────────────────────────────────────────────────

    private string BuildEventsUrl(string calendarId, string? syncToken, string? pageToken)
    {
        var query = new List<string>
        {
            "singleEvents=false",                 // keep recurrence masters + RRULE (google §5, §7)
            $"maxResults={MaxResults}",
        };

        if (syncToken is { Length: > 0 })
        {
            // Incremental: syncToken is mutually exclusive with timeMin/timeMax/showDeleted; deleted entries are
            // always included (google §6, §13).
            query.Add($"syncToken={Uri.EscapeDataString(syncToken)}");
        }
        else
        {
            // First (full) sync: bound the payload with the configured active window.
            var now = DateTimeOffset.UtcNow;
            var timeMin = now.AddMonths(-Math.Max(0, _config.SyncWindowMonthsPast));
            var timeMax = now.AddMonths(Math.Max(1, _config.SyncWindowMonthsFuture));
            query.Add($"timeMin={Uri.EscapeDataString(timeMin.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}");
            query.Add($"timeMax={Uri.EscapeDataString(timeMax.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}");
            query.Add("showDeleted=false");       // full sync doesn't need tombstones
        }

        if (pageToken is { Length: > 0 })
            query.Add($"pageToken={Uri.EscapeDataString(pageToken)}");

        var calId = Uri.EscapeDataString(calendarId);
        return $"{ApiBase}/calendars/{calId}/events?{string.Join("&", query)}";
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Events.list GET that turns a <c>410 GONE</c> into <see cref="SyncResetRequiredException"/>.</summary>
    private async Task<JsonDocument> GetEventsPageAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await _host.Auth.ApplyAsync(request, ReadScopes, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // An invalidated syncToken (expiry, ACL change, or a periodically-rebuilt Holidays calendar) → the host
        // drops the token, wipes the calendar cache, and re-runs a full sync (google §6, §13).
        if (response.StatusCode == HttpStatusCode.Gone)
            throw new SyncResetRequiredException(
                "Google sync token is no longer valid (410 GONE); a full resync is required.");

        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    /// <summary>Plain authenticated GET → parsed JSON (CalendarList and other non-delta reads).</summary>
    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await _host.Auth.ApplyAsync(request, ReadScopes, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    /// <summary>401 → the broker's bearer was rejected; surface a clear auth failure (google §3, §15).</summary>
    private static Task EnsureAuthOkAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(
                "Google Calendar authentication failed (401). The broker's access token was rejected; " +
                "re-consent may be required.");
        return Task.CompletedTask;
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
