using System.Net;
using System.Text;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Google.Tests;

/// <summary>
/// A canned Google Calendar API: an <see cref="HttpMessageHandler"/> that matches GET requests by a substring
/// of the request URL (so <c>calendarList</c> vs <c>events</c>, and pages distinguished by <c>pageToken</c>/
/// <c>syncToken</c>, route distinctly) and replays canned JSON. No live network (google §15).
/// </summary>
internal sealed class GoogleApiStub : HttpMessageHandler
{
    private readonly List<Route> _routes = new();
    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Register a JSON response for a GET whose URL contains <paramref name="urlContains"/>.</summary>
    public GoogleApiStub On(string urlContains, HttpStatusCode status, string? json)
    {
        _routes.Add(new Route(urlContains, status, json));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));

        foreach (var route in _routes)
        {
            if (!url.Contains(route.UrlContains, StringComparison.Ordinal))
                continue;

            var response = new HttpResponseMessage(route.Status);
            if (route.Json is not null)
                response.Content = new StringContent(route.Json, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }

        throw new InvalidOperationException($"No stub route for {request.Method.Method} {url}.");
    }

    private sealed record Route(string UrlContains, HttpStatusCode Status, string? Json);
}

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization);

/// <summary>
/// A fake broker returning a canned bearer handle and applying <c>Authorization: Bearer &lt;token&gt;</c>
/// (SDK-CONTRACT.md §3). Mirrors the real broker's <see cref="AuthScheme.OAuth2Pkce"/> behavior without a
/// vault or any real OAuth round-trip — the plugin never sees secrets.
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
