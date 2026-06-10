using System.Net;
using System.Text;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Duffel.Tests;

/// <summary>
/// A canned Duffel API: an <see cref="HttpMessageHandler"/> matching requests by a substring of the URL
/// (<c>/air/offer_requests</c> vs <c>/stays/search</c>) and replaying canned JSON. No live network
/// (travel-fares-plugin.md §13: integration runs against fixtures, never a live token).
/// </summary>
internal sealed class DuffelApiStub : HttpMessageHandler
{
    private readonly List<Route> _routes = new();
    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Register a response for a request whose URL contains <paramref name="urlContains"/>.</summary>
    public DuffelApiStub On(string urlContains, HttpStatusCode status, string? json)
    {
        _routes.Add(new Route(urlContains, status, json));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Duffel-Version", out var v) ? string.Join(",", v) : null,
            body));

        foreach (var route in _routes)
        {
            if (!url.Contains(route.UrlContains, StringComparison.Ordinal))
                continue;

            var response = new HttpResponseMessage(route.Status);
            if (route.Json is not null)
                response.Content = new StringContent(route.Json, Encoding.UTF8, "application/json");
            return response;
        }

        throw new InvalidOperationException($"No stub route for {request.Method.Method} {url}.");
    }

    private sealed record Route(string UrlContains, HttpStatusCode Status, string? Json);
}

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string? DuffelVersion, string? Body);

/// <summary>
/// A fake auth broker mirroring the real <see cref="AuthScheme.ApiKey"/> behavior: it applies
/// <c>Authorization: Bearer &lt;token&gt;</c> per the Duffel manifest's <c>format</c>, without a vault. The
/// plugin never sees the key — exactly the contract the real broker honors (travel-fares-plugin.md §3).
/// </summary>
internal sealed class FakeApiKeyBroker : IAuthBroker
{
    private readonly string _token;

    public FakeApiKeyBroker(string token = "duffel_test_token") => _token = token;

    public Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
        Task.FromResult(new AuthHandle(AuthScheme.ApiKey, _token, null, null, null));

    public Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        return Task.CompletedTask;
    }
}
