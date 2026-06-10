using System.Globalization;
using System.Net.Http.Json;

namespace Calendar.Web.Services;

/// <summary>Typed client over the host's REST API (API.md). One per WASM session.</summary>
public sealed class CalendarApiClient
{
    private readonly HttpClient _http;

    public CalendarApiClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<EventDto>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"api/events?from={Iso(from)}&to={Iso(to)}";
        return await _http.GetFromJsonAsync<List<EventDto>>(url, ct) ?? new List<EventDto>();
    }

    /// <summary>Map pins: events with a resolved place overlapping [from, to) (ARCHITECTURE §7).</summary>
    public async Task<IReadOnlyList<MapEventDto>> GetMapEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"api/map/events?from={Iso(from)}&to={Iso(to)}";
        return await _http.GetFromJsonAsync<List<MapEventDto>>(url, ct) ?? new List<MapEventDto>();
    }

    // ── Reminders (Phase 6 polish): created in the event editor, fired by the hosted sweep. ──

    /// <summary>All reminders (pending + fired), joined with their events. Degrades to empty.</summary>
    public async Task<IReadOnlyList<ReminderDto>> GetRemindersAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<ReminderDto>>("api/reminders", ct) ?? new List<ReminderDto>();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return new List<ReminderDto>();   // reminders are progressive enhancement in the editor.
        }
    }

    /// <summary>Create a reminder firing <paramref name="leadMinutes"/> before the event starts.</summary>
    public async Task<ReminderDto?> CreateReminderAsync(Guid eventId, int leadMinutes, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/events/{eventId}/reminders", new { leadMinutes }, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<ReminderDto>(ct);
    }

    /// <summary>Delete a reminder (<c>DELETE /api/reminders/{id}</c>).</summary>
    public async Task<bool> DeleteReminderAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/reminders/{id}", ct);
        return response.IsSuccessStatusCode;
    }

    // ── Marketplace (Phase 6): install signed bundles, uninstall (gated server-side). ──

    /// <summary>Install a bundle from a URL; returns the plugin or the gate failure message.</summary>
    public async Task<(InstalledPluginDto? Plugin, string? Error)> InstallPluginAsync(
        InstallPluginBody body, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/plugins/install", body, ct);
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<InstalledPluginDto>(ct), null);
        return (null, await ReadErrorAsync(response, ct));
    }

    /// <summary>Uninstall a plugin; the error carries the 409 reason when accounts still use it.</summary>
    public async Task<(bool Ok, string? Error)> UninstallPluginAsync(string pluginId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/plugins/{Uri.EscapeDataString(pluginId)}", ct);
        if (response.IsSuccessStatusCode)
            return (true, null);
        return (false, await ReadErrorAsync(response, ct));
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            return body?.Error ?? $"Request failed ({(int)response.StatusCode}).";
        }
        catch
        {
            return $"Request failed ({(int)response.StatusCode}).";
        }
    }

    private sealed record ErrorBody(string? Error);

    /// <summary>Search stored events by title/location (<c>GET /api/search</c>, Phase 6 polish).</summary>
    public async Task<IReadOnlyList<SearchResultDto>> SearchEventsAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/search?q={Uri.EscapeDataString(query)}&limit={limit}";
            return await _http.GetFromJsonAsync<List<SearchResultDto>>(url, ct) ?? new List<SearchResultDto>();
        }
        catch (HttpRequestException)
        {
            return new List<SearchResultDto>();
        }
    }

    /// <summary>Trip routes overlapping [from, to) (<c>GET /api/map/trips</c>, Phase 5). Degrades to empty.</summary>
    public async Task<IReadOnlyList<TripRouteDto>> GetTripRoutesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/map/trips?from={Iso(from)}&to={Iso(to)}";
            return await _http.GetFromJsonAsync<List<TripRouteDto>>(url, ct) ?? new List<TripRouteDto>();
        }
        catch (HttpRequestException)
        {
            return new List<TripRouteDto>();    // trips are an overlay: a failure paints nothing, never breaks the map.
        }
    }

    /// <summary>The resolved MapLibre basemap style (or a bundled default) for the Map view (geo-tiles §2).</summary>
    public async Task<MapStyleDto> GetMapStyleAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<MapStyleDto>("api/map/style", ct)
            ?? new MapStyleDto(null, null, "© OpenStreetMap contributors", "vector", false);

    public async Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<AccountDto>>("api/accounts", ct) ?? new List<AccountDto>();

    public async Task<IReadOnlyList<CalendarDto>> GetCalendarsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<CalendarDto>>("api/calendars", ct) ?? new List<CalendarDto>();

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<CategoryDto>>("api/categories", ct) ?? new List<CategoryDto>();

    public async Task<bool> ConnectIcsAsync(string feedUrl, string? name, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "api/accounts",
            new ConnectAccountBody(null, feedUrl, name, RefreshMinutes: 60, ForceCategory: null, Config: null), ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Installed plugins (<c>GET /api/plugins</c>) — the Add-account UI offers each running provider.</summary>
    public async Task<IReadOnlyList<PluginDto>> GetPluginsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<PluginDto>>("api/plugins", ct) ?? new List<PluginDto>();

    /// <summary>
    /// Generic connect (<c>POST /api/accounts</c>): scheme-none/credentialed plugins return a finished account;
    /// OAuth plugins return an <see cref="AuthChallengeDto"/> the caller redirects the browser to.
    /// </summary>
    public async Task<ConnectAccountResult?> ConnectAccountAsync(
        string pluginId, string? name = null, Dictionary<string, string>? config = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            "api/accounts", new ConnectAccountBody(pluginId, null, name, null, null, config), ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<ConnectAccountResult>(ct);
    }

    public Task SetCalendarVisibilityAsync(Guid id, bool isVisible, CancellationToken ct = default) =>
        _http.PatchAsJsonAsync($"api/calendars/{id}", new { isVisible }, ct);

    public Task SetCategoryVisibilityAsync(Guid id, bool isVisible, CancellationToken ct = default) =>
        _http.PatchAsJsonAsync($"api/categories/{id}", new { isVisible }, ct);

    // ── Event write-back (ARCHITECTURE §8/§10). Gated server-side behind calendar.write; a write to a
    //    read-only calendar returns 409. The host either applies the write to the bound plugin or, offline,
    //    queues it in the outbox (Disposition=Queued). ──

    /// <summary>Create an event on a writable calendar (<c>POST /api/events</c>).</summary>
    public async Task<EventWriteResultDto> CreateEventAsync(EventWriteBody body, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/events", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventWriteResultDto>(ct))!;
    }

    /// <summary>Edit an existing event (<c>PATCH /api/events/{id}</c>). The calendar cannot change here.</summary>
    public async Task<EventWriteResultDto> UpdateEventAsync(Guid id, EventWriteBody body, CancellationToken ct = default)
    {
        var response = await _http.PatchAsJsonAsync($"api/events/{id}", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventWriteResultDto>(ct))!;
    }

    /// <summary>Delete an event (<c>DELETE /api/events/{id}</c>).</summary>
    public async Task<EventWriteResultDto> DeleteEventAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/events/{id}", ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventWriteResultDto>(ct))!;
    }

    /// <summary>Queued-writes summary for the "↻ (N)" footer indicator (<c>GET /api/sync/outbox</c>, §8).</summary>
    public async Task<OutboxStatusDto> GetOutboxStatusAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<OutboxStatusDto>("api/sync/outbox", ct)
            ?? new OutboxStatusDto(0, 0, null);

    // ── Sharing (ARCHITECTURE §16). Owner-facing management; the public feed lives at /share/{token}.ics. ──

    /// <summary>List the owner's active shares (<c>GET /api/shares</c>).</summary>
    public async Task<IReadOnlyList<ShareDto>> GetSharesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<ShareDto>>("api/shares", ct) ?? new List<ShareDto>();

    /// <summary>Create a tokenized share link (<c>POST /api/shares</c>); returns the public feed URL + token.</summary>
    public async Task<ShareDto> CreateShareAsync(CreateShareBody body, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/shares", body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShareDto>(ct))!;
    }

    /// <summary>Revoke a share (<c>DELETE /api/shares/{id}</c>); the public feed 404s afterwards.</summary>
    public async Task RevokeShareAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/shares/{id}", ct);
        response.EnsureSuccessStatusCode();
    }

    // ── Travel fares: multi-month overlay + watches + notifications (ARCHITECTURE §14, UI-WIREFRAMES §3) ──

    /// <summary>
    /// The multi-month flight cheapest-date overlay (<c>GET /api/fares/overlay?kind=flight</c>). Returns an overlay
    /// with an empty cell map when no pricing provider is configured — the planner then paints nothing.
    /// </summary>
    public async Task<FareOverlayDto> GetFlightOverlayAsync(
        string origin, string dest, DateOnly from, DateOnly to, int pax = 1, string? currency = null,
        CancellationToken ct = default)
    {
        var url = $"api/fares/overlay?kind=flight&from={Day(from)}&to={Day(to)}" +
                  $"&origin={Uri.EscapeDataString(origin)}&dest={Uri.EscapeDataString(dest)}&pax={pax}" +
                  (currency is null ? "" : $"&currency={Uri.EscapeDataString(currency)}");
        return await GetOverlayAsync(url, "flight", currency, ct).ConfigureAwait(false);
    }

    /// <summary>The multi-month stay nightly-rate overlay (<c>GET /api/fares/overlay?kind=stay</c>).</summary>
    public async Task<FareOverlayDto> GetStayOverlayAsync(
        double lat, double lng, DateOnly from, DateOnly to, int nights = 1, int pax = 1, string? currency = null,
        CancellationToken ct = default)
    {
        var url = $"api/fares/overlay?kind=stay&from={Day(from)}&to={Day(to)}" +
                  $"&lat={Num(lat)}&lng={Num(lng)}&nights={nights}&pax={pax}" +
                  (currency is null ? "" : $"&currency={Uri.EscapeDataString(currency)}");
        return await GetOverlayAsync(url, "stay", currency, ct).ConfigureAwait(false);
    }

    private async Task<FareOverlayDto> GetOverlayAsync(string url, string kind, string? currency, CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<FareOverlayDto>(url, ct).ConfigureAwait(false)
                ?? Empty(kind, currency);
        }
        catch (HttpRequestException)
        {
            // Pricing degrades gracefully: a transient overlay failure paints nothing rather than breaking the grid.
            return Empty(kind, currency);
        }

        static FareOverlayDto Empty(string kind, string? currency) =>
            new(kind, currency ?? "USD", null, Array.Empty<OverlayCellDto>());
    }

    /// <summary>List active fare watches with their latest low (<c>GET /api/fares/watches</c>).</summary>
    public async Task<IReadOnlyList<FareWatchDto>> GetFareWatchesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<FareWatchDto>>("api/fares/watches", ct) ?? new List<FareWatchDto>();

    /// <summary>A watch's price series for its sparkline (<c>GET /api/fares/watches/{id}/history</c>).</summary>
    public async Task<IReadOnlyList<FareSampleDto>> GetFareHistoryAsync(Guid id, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<FareSampleDto>>($"api/fares/watches/{id}/history", ct) ?? new List<FareSampleDto>();

    /// <summary>Create a fare watch (<c>POST /api/fares/watches</c>).</summary>
    public async Task<FareWatchDto?> CreateFareWatchAsync(CreateFareWatchBody body, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("api/fares/watches", body, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<FareWatchDto>(ct);
    }

    /// <summary>Delete a fare watch (<c>DELETE /api/fares/watches/{id}</c>).</summary>
    public async Task<bool> DeleteFareWatchAsync(Guid id, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"api/fares/watches/{id}", ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>The in-app notification log, newest-first (<c>GET /api/notifications</c>) — fare-drop/target alerts.</summary>
    public async Task<IReadOnlyList<NotificationDto>> GetNotificationsAsync(int limit = 50, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<NotificationDto>>($"api/notifications?limit={limit}", ct) ?? new List<NotificationDto>();

    private static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Iso(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
}
