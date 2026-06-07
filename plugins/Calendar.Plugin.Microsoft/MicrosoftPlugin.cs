using System.Net;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Microsoft;

/// <summary>
/// The Microsoft Graph plugin (microsoft-graph-plugin.md): an OAuth <c>calendar.read</c> source over Microsoft
/// Graph's REST API covering personal Microsoft accounts and work/school (Entra ID) mailboxes through the same
/// <c>common</c> authority. OAuth (host broker) → enumerate (<c>/me/calendars</c>) → initial windowed delta
/// (<c>/me/calendars/{id}/calendarView/delta</c>) → persist <c>@odata.deltaLink</c> → replay it (incremental)
/// → normalize. An expired/invalid deltaLink returns <c>410 Gone</c> (<c>syncStateNotFound</c>) and throws
/// <see cref="SyncResetRequiredException"/> so the host wipes the token and re-runs a full windowed sync.
///
/// The plugin never sees a token: it builds each request and calls <c>IPluginHost.Auth.ApplyAsync</c> for a
/// fresh bearer (it never instantiates MSAL or holds secrets — SDK-CONTRACT.md §3, microsoft-graph-plugin.md
/// §3, §11). It talks raw REST/JSON over the host's egress-filtered <c>HttpClient</c> so the whole path is
/// stub-testable without the Graph SDK or a live tenant.
/// </summary>
public sealed class MicrosoftPlugin : ICalendarSource
{
    public const string Id = "org.unifiedcalendar.microsoft";

    /// <summary>The least-privilege read scope requested from the broker (microsoft-graph-plugin.md §3).</summary>
    private static readonly IReadOnlyList<string> ReadScopes =
        new[] { "https://graph.microsoft.com/Calendars.Read" };

    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int MaxPageSize = 50;

    /// <summary>
    /// Safety bound on pages per sync round. Graph's <c>calendarView/delta</c> has reported infinite-paging
    /// loops (rotating <c>$skiptoken</c>, identical payload, no terminal deltaLink — microsoft-graph-plugin.md
    /// §12, §14); this cap halts the round rather than hanging.
    /// </summary>
    private const int MaxPagesPerRound = 1000;

    private IPluginHost _host = default!;
    private MicrosoftConfig _config = default!;

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "Microsoft 365 / Outlook",
        Version: "0.1.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.CalendarRead },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(
            Scheme: AuthScheme.OAuth2Pkce,
            AuthorizationUrl: "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
            TokenUrl: "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            Authority: "https://login.microsoftonline.com/common",
            Scopes: new[]
            {
                "https://graph.microsoft.com/Calendars.Read", "offline_access", "openid", "profile",
            }),
        Network: new NetworkSpec(new[] { "graph.microsoft.com", "login.microsoftonline.com" }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"syncWindowMonthsBack":{"type":"integer","default":1,"minimum":0},"syncWindowMonthsForward":{"type":"integer","default":12,"minimum":1}},"required":[]}""",
            Array.Empty<string>()));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        _config = host.GetConfig<MicrosoftConfig>();
        return Task.CompletedTask;
    }

    // ── calendar.read ───────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var calendars = new List<RemoteCalendar>();

        // $select trims the payload to the fields we normalize (microsoft-graph-plugin.md §4, §11).
        string? next =
            $"{GraphBase}/me/calendars?$select=id,name,color,hexColor,canEdit,owner,isDefaultCalendar,changeKey";

        var guard = 0;
        while (next is not null && guard++ < MaxPagesPerRound)
        {
            using var doc = await GetJsonAsync(client, next, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    calendars.Add(MicrosoftNormalizer.NormalizeCalendar(item));

            next = GetString(root, "@odata.nextLink");
        }

        return calendars;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct)
    {
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose

        var upserts = new List<RemoteEvent>();
        var deletes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // dedupe guard for paging-loop quirk (§12)

        // Initial round: build the windowed calendarView/delta. Incremental round: GET the saved deltaLink
        // verbatim (it already encodes the window + cursor; do NOT append startDateTime/endDateTime/$select).
        var url = string.IsNullOrEmpty(syncToken)
            ? BuildInitialDeltaUrl(remoteCalendarId)
            : syncToken;

        string? deltaLink = null;
        var pages = 0;

        while (url is not null)
        {
            if (++pages > MaxPagesPerRound)
                throw new InvalidOperationException(
                    "Microsoft Graph calendarView/delta exceeded the page safety bound; aborting to avoid an " +
                    "infinite paging loop (microsoft-graph-plugin.md §12).");

            using var doc = await GetDeltaPageAsync(client, url, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var id = GetString(item, "id");

                    // Tombstone: a delta item carrying @removed → delete by id (microsoft-graph-plugin.md §6).
                    if (item.TryGetProperty("@removed", out _))
                    {
                        if (id is not null && seen.Add($"d:{id}"))
                            deletes.Add(id);
                        continue;
                    }

                    var ev = MicrosoftNormalizer.NormalizeEvent(item);
                    if (!seen.Add($"u:{ev.RemoteId}"))
                        continue; // already processed this id in this round (loop guard)

                    if (ev.Status == EventStatus.Cancelled)
                        deletes.Add(ev.RemoteId); // isCancelled → delete tombstone
                    else
                        upserts.Add(ev);
                }
            }

            // More pages → @odata.nextLink; round complete → @odata.deltaLink (persist verbatim as the token).
            var nextLink = GetString(root, "@odata.nextLink");
            deltaLink = GetString(root, "@odata.deltaLink") ?? deltaLink;
            url = nextLink; // when null and deltaLink present, the round is done
        }

        // The deltaLink always arrives on the terminal page; preserve the prior token defensively if absent.
        return new SyncResult(upserts, deletes, deltaLink ?? syncToken ?? string.Empty);
    }

    // ── Request building ──────────────────────────────────────────────────────────────────────────────

    private string BuildInitialDeltaUrl(string calendarId)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMonths(-Math.Max(0, _config.SyncWindowMonthsBack));
        var end = now.AddMonths(Math.Max(1, _config.SyncWindowMonthsForward));

        var startStr = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endStr = end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Target the specific calendar (a non-default calendar requires the scoped path; /me/... targets the
        // default). $select is NOT supported on calendarView/delta (microsoft-graph-plugin.md §6, §11).
        var calId = Uri.EscapeDataString(calendarId);
        return $"{GraphBase}/me/calendars/{calId}/calendarView/delta" +
               $"?startDateTime={Uri.EscapeDataString(startStr)}&endDateTime={Uri.EscapeDataString(endStr)}";
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>calendarView/delta</c> GET that turns a <c>410 Gone</c> (<c>syncStateNotFound</c>) into
    /// <see cref="SyncResetRequiredException"/> (microsoft-graph-plugin.md §6, §14). Sets the
    /// <c>Prefer: outlook.timezone="UTC"</c> + <c>odata.maxpagesize</c> headers and immutable-id preference.
    /// </summary>
    private async Task<JsonDocument> GetDeltaPageAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Force UTC so normalization is deterministic; cap page size; ask for immutable ids so an event id
        // survives a folder move (microsoft-graph-plugin.md §6, §8, §11).
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.timezone=\"UTC\"");
        request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={MaxPageSize}");
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");

        await _host.Auth.ApplyAsync(request, ReadScopes, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // An invalidated deltaLink (token aged out / window or mailbox state diverged) → the host drops the
        // token, wipes the calendar cache, and re-runs a full windowed sync (microsoft-graph-plugin.md §6, §14).
        if (response.StatusCode == HttpStatusCode.Gone)
            throw new SyncResetRequiredException(
                "Microsoft Graph delta token is no longer valid (410 Gone / syncStateNotFound); a full resync " +
                "is required.");

        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    /// <summary>Plain authenticated GET → parsed JSON (calendar enumeration and other non-delta reads).</summary>
    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
        await _host.Auth.ApplyAsync(request, ReadScopes, ct).ConfigureAwait(false);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureAuthOkAsync(response).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// 401 → the broker's bearer was rejected (revoked refresh token / re-consent needed). Work tenants may also
    /// hit an admin-consent wall at connect time (<c>AADSTS65001</c>/<c>90094</c>) — that is surfaced by the
    /// broker, not here (microsoft-graph-plugin.md §3, §14).
    /// </summary>
    private static Task EnsureAuthOkAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException(
                "Microsoft Graph authentication failed (401). The broker's access token was rejected; " +
                "reconnect / re-consent may be required.");
        return Task.CompletedTask;
    }

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
