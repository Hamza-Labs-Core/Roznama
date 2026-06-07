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
            "api/accounts", new ConnectIcsRequest(feedUrl, name, RefreshMinutes: 60, ForceCategory: null), ct);
        return response.IsSuccessStatusCode;
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

    private static string Iso(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
}
