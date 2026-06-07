using System.Net;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that records every request and answers from a caller-supplied
/// responder — no live network (PLUGIN-HOST.md §11 "no live third-party credentials are needed in CI").
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    /// <summary>Every request seen, with its already-read body — lets a test assert what was actually sent.</summary>
    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) =>
        _responder = responder;

    /// <summary>Convenience: always answer with this JSON body and status.</summary>
    public static StubHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, Headers(request), body));
        return _responder(request, body);
    }

    private static IReadOnlyDictionary<string, string> Headers(HttpRequestMessage request)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            dict[h.Key] = string.Join(",", h.Value);
        return dict;
    }
}

/// <summary>A captured outbound request: method, URI, headers, and form/JSON body.</summary>
public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body);
