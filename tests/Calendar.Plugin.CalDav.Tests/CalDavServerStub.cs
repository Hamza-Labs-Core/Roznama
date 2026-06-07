using System.Net;
using System.Text;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.CalDav.Tests;

/// <summary>
/// A canned WebDAV server: an <see cref="HttpMessageHandler"/> that matches requests by HTTP method + a body
/// substring (so PROPFIND for principal vs home-set, REPORT sync-collection vs calendar-query are routed
/// distinctly) and replays canned XML. No live network (caldav-plugin.md §13).
/// </summary>
internal sealed class CalDavServerStub : HttpMessageHandler
{
    private readonly List<Route> _routes = new();
    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Register a response for a method whose request body contains <paramref name="bodyContains"/>.</summary>
    public CalDavServerStub On(
        string method, string bodyContains, HttpStatusCode status, string? xml,
        Action<HttpResponseMessage>? mutate = null)
    {
        _routes.Add(new Route(method, bodyContains, status, xml, mutate));
        return this;
    }

    /// <summary>Register a response keyed only on the HTTP method (e.g. PUT/DELETE that carry no XML body).</summary>
    public CalDavServerStub OnMethod(
        string method, HttpStatusCode status, string? xml = null, Action<HttpResponseMessage>? mutate = null)
        => On(method, "", status, xml, mutate);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri!,
            body,
            request.Headers.TryGetValues("Depth", out var d) ? string.Join(",", d) : null,
            request.Headers.TryGetValues("If-Match", out var im) ? string.Join(",", im) : null,
            request.Headers.TryGetValues("If-None-Match", out var inm) ? string.Join(",", inm) : null,
            request.Headers.Authorization?.ToString()));

        foreach (var route in _routes)
        {
            if (!string.Equals(route.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (route.BodyContains.Length > 0 && !body.Contains(route.BodyContains, StringComparison.Ordinal))
                continue;

            var response = new HttpResponseMessage(route.Status);
            if (route.Xml is not null)
                response.Content = new StringContent(route.Xml, Encoding.UTF8, "application/xml");
            route.Mutate?.Invoke(response);
            return response;
        }

        throw new InvalidOperationException(
            $"No stub route for {request.Method.Method} {request.RequestUri} (body: {Trim(body)}).");
    }

    private static string Trim(string s) => s.Length <= 80 ? s : s[..80] + "…";

    private sealed record Route(
        string Method, string BodyContains, HttpStatusCode Status, string? Xml, Action<HttpResponseMessage>? Mutate);
}

internal sealed record RecordedRequest(
    string Method, Uri Uri, string Body, string? Depth, string? IfMatch, string? IfNoneMatch, string? Authorization);

/// <summary>
/// A fake broker returning a canned Basic handle/token and applying a deterministic Basic header (SDK-CONTRACT
/// §3). Mirrors the real broker's <see cref="AuthScheme.AppPassword"/> behavior without a vault.
/// </summary>
internal sealed class FakeBasicAuthBroker : IAuthBroker
{
    private readonly string _username;
    private readonly string _password;

    public FakeBasicAuthBroker(string username = "me@test", string password = "app-specific-pw")
    {
        _username = username;
        _password = password;
    }

    public Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
        Task.FromResult(new AuthHandle(AuthScheme.AppPassword, null, _username, _password, null));

    public Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", basic);
        return Task.CompletedTask;
    }
}
