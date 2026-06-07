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

    private static string Iso(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
}
