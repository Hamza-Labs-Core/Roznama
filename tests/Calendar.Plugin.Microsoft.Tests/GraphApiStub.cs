using System.Net;
using System.Text;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Microsoft.Tests;

/// <summary>
/// A canned Microsoft Graph API: an <see cref="HttpMessageHandler"/> that matches GET requests by a substring
/// of the request URL (so <c>/me/calendars</c> vs <c>calendarView/delta</c>, and pages distinguished by their
/// <c>$skiptoken</c>/<c>$deltatoken</c>, route distinctly) and replays canned JSON. No live network or tenant
/// (microsoft-graph-plugin.md §14). Each route is consumed once when <c>once: true</c> so a deltaLink replayed
/// twice can return different bodies (initial → incremental).
/// </summary>
internal sealed class GraphApiStub : HttpMessageHandler
{
    private readonly List<Route> _routes = new();
    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Register a JSON response for a GET whose URL contains <paramref name="urlContains"/>.</summary>
    public GraphApiStub On(string urlContains, HttpStatusCode status, string? json, bool once = false)
    {
        _routes.Add(new Route(urlContains, status, json, once));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var preferHeaders = request.Headers.TryGetValues("Prefer", out var prefers)
            ? string.Join("; ", prefers)
            : null;
        Requests.Add(new RecordedRequest(
            request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), preferHeaders));

        foreach (var route in _routes)
        {
            if (route.Consumed || !url.Contains(route.UrlContains, StringComparison.Ordinal))
                continue;

            if (route.Once)
                route.Consumed = true;

            var response = new HttpResponseMessage(route.Status);
            if (route.Json is not null)
                response.Content = new StringContent(route.Json, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }

        throw new InvalidOperationException($"No stub route for {request.Method.Method} {url}.");
    }

    private sealed class Route(string urlContains, HttpStatusCode status, string? json, bool once)
    {
        public string UrlContains { get; } = urlContains;
        public HttpStatusCode Status { get; } = status;
        public string? Json { get; } = json;
        public bool Once { get; } = once;
        public bool Consumed { get; set; }
    }
}

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? Prefer);

/// <summary>
/// A fake broker returning a canned bearer handle and applying <c>Authorization: Bearer &lt;token&gt;</c>
/// (SDK-CONTRACT.md §3). Mirrors the real broker's <see cref="AuthScheme.OAuth2Pkce"/> behavior without a
/// vault or any MSAL round-trip — the plugin never sees secrets (microsoft-graph-plugin.md §3, §11).
/// </summary>
internal sealed class FakeBearerAuthBroker : IAuthBroker
{
    private readonly string _token;

    public FakeBearerAuthBroker(string token = "canned-access-token") => _token = token;

    public Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
        Task.FromResult(new AuthHandle(
            AuthScheme.OAuth2Pkce, _token, null, null, DateTimeOffset.UtcNow.AddMinutes(30)));

    public Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return Task.CompletedTask;
    }
}
