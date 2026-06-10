using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Ics;

/// <summary>
/// The ICS / iCalendar feed plugin (ics-plugin.md): any feed URL becomes a read-only <c>calendar.read</c>
/// source with zero OAuth. Fetch (conditional) → parse → diff against the last snapshot → upsert/delete.
/// One feed = one calendar; the self-minted <c>etag|lastmod|bodyhash</c> token stands in for the delta
/// cursor a feed never provides.
/// </summary>
public sealed class IcsPlugin : ICalendarSource
{
    public const string Id = "org.unifiedcalendar.ics";

    private IPluginHost _host = default!;
    private IcsConfig _config = default!;

    public PluginManifest Manifest { get; } = new(
        Id: Id,
        Name: "ICS / iCalendar feed",
        Version: "1.0.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.CalendarRead },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(AuthScheme.None),
        Network: new NetworkSpec(new[] { "*" }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"feedUrl":{"type":"string"},"refreshMinutes":{"type":"integer","default":60,"minimum":15}},"required":["feedUrl"]}""",
            new[] { "feedUrl" }));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        _config = host.GetConfig<IcsConfig>();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct)
    {
        var url = RewriteWebcal(_config.FeedUrl);
        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var (body, _, _, status) = await FetchAsync(client, url, null, null, ct).ConfigureAwait(false);

        string? name = _config.CalendarName;
        string? color = null;
        if (status == HttpStatusCode.OK && body is not null)
        {
            var parsed = IcsNormalizer.Parse(Decode(body), _config.ForceCategory);
            name ??= parsed.CalendarName;
            color = parsed.Color;
        }

        return new[]
        {
            new RemoteCalendar(url, name ?? "ICS feed", color, IsReadOnly: true)
        };
    }

    public async Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct)
    {
        var url = RewriteWebcal(remoteCalendarId);
        var (priorEtag, priorLastMod, priorBodyHash) = ParseToken(syncToken);

        var client = _host.CreateClient(); // host owns the client lifetime — do not dispose
        var (body, etag, lastMod, status) =
            await FetchAsync(client, url, priorEtag, priorLastMod, ct).ConfigureAwait(false);

        // Unchanged → empty delta, same cursor (the common, near-free case).
        if (status == HttpStatusCode.NotModified || body is null)
            return new SyncResult(Array.Empty<RemoteEvent>(), Array.Empty<string>(), syncToken ?? string.Empty);

        var bodyHash = Convert.ToBase64String(SHA256.HashData(body));
        var newToken = $"{etag}|{lastMod}|{bodyHash}";

        // Validators lied / absent but bytes are identical → skip parsing entirely.
        if (bodyHash == priorBodyHash)
            return new SyncResult(Array.Empty<RemoteEvent>(), Array.Empty<string>(), newToken);

        var parsed = IcsNormalizer.Parse(Decode(body), _config.ForceCategory);

        var prior = await LoadSnapshotAsync(url, ct).ConfigureAwait(false);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var upserts = new List<RemoteEvent>();
        var deletes = new List<string>();

        foreach (var ev in parsed.Events)
        {
            if (ev.Status == EventStatus.Cancelled)
            {
                deletes.Add(ev.RemoteId); // tombstone, even if still present in the body
                continue;
            }

            next[ev.RemoteId] = ev.ChangeTag ?? string.Empty;
            if (!prior.TryGetValue(ev.RemoteId, out var priorTag) || priorTag != (ev.ChangeTag ?? string.Empty))
                upserts.Add(ev);
        }

        // Anything in the prior snapshot but gone from this one was deleted upstream.
        foreach (var remoteId in prior.Keys)
            if (!next.ContainsKey(remoteId))
                deletes.Add(remoteId);

        await SaveSnapshotAsync(url, next, ct).ConfigureAwait(false);
        return new SyncResult(upserts, deletes, newToken);
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(byte[]? Body, string? ETag, string? LastModified, HttpStatusCode Status)> FetchAsync(
        HttpClient client, string url, string? ifNoneMatch, string? ifModifiedSince, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(ifNoneMatch))
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (!string.IsNullOrEmpty(ifModifiedSince))
            request.Headers.TryAddWithoutValidation("If-Modified-Since", ifModifiedSince);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return (null, ifNoneMatch, ifModifiedSince, HttpStatusCode.NotModified);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var etag = response.Headers.ETag?.ToString();
        var lastMod = response.Content.Headers.LastModified?.ToString("R");
        return (body, etag, lastMod, response.StatusCode);
    }

    // ── Snapshot cache (for delta synthesis) ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> LoadSnapshotAsync(string url, CancellationToken ct)
    {
        var bytes = await _host.Cache.GetAsync(SnapshotKey(url), ct).ConfigureAwait(false);
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

    private Task SaveSnapshotAsync(string url, Dictionary<string, string> snapshot, CancellationToken ct) =>
        _host.Cache.SetAsync(SnapshotKey(url), JsonSerializer.SerializeToUtf8Bytes(snapshot), ttl: null, ct);

    private static string SnapshotKey(string url) => $"snapshot:{url}";

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rewrite Apple's <c>webcal(s)://</c> subscription pseudo-scheme to HTTPS (ics-plugin.md §4).</summary>
    internal static string RewriteWebcal(string url)
    {
        if (url.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url["webcals://".Length..];
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
            return "https://" + url["webcal://".Length..];
        return url;
    }

    private static (string? ETag, string? LastModified, string? BodyHash) ParseToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return (null, null, null);
        var parts = token.Split('|');
        return (
            parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null,
            parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null,
            parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
    }

    private static string Decode(byte[] body)
    {
        // Strip a UTF-8 BOM if present, then decode as UTF-8 (ics-plugin.md §4).
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
            return Encoding.UTF8.GetString(body, 3, body.Length - 3);
        return Encoding.UTF8.GetString(body);
    }
}
