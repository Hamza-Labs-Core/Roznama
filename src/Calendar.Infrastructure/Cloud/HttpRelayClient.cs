using System.Net.Http.Json;
using Calendar.Application.Cloud;

namespace Calendar.Infrastructure.Cloud;

/// <summary>
/// The device-side HTTP client for any relay node speaking the <c>/relay/*</c> protocol (every host
/// self-serves it). Payloads travel as base64 inside small JSON envelopes; the token rides a header.
/// </summary>
public sealed class HttpRelayClient : IRelayClient
{
    private const string TokenHeader = "X-Relay-Token";

    private readonly HttpClient _http;

    public HttpRelayClient(HttpClient http) => _http = http;

    public async Task<(Guid SpaceId, string Token)> CreateSpaceAsync(string relayUrl, CancellationToken ct)
    {
        using var response = await _http.PostAsync(Url(relayUrl, "relay/spaces"), content: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<SpaceCreated>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Relay returned an empty space-creation response.");
        return (created.SpaceId, created.Token);
    }

    public async Task<long> PushAsync(
        string relayUrl, Guid spaceId, string token, Guid deviceId, byte[] payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(relayUrl, $"relay/{spaceId}/changes"))
        {
            Content = JsonContent.Create(new PushBody(deviceId, payload)),
        };
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var pushed = await response.Content.ReadFromJsonAsync<Pushed>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Relay returned an empty push response.");
        return pushed.Seq;
    }

    public async Task<IReadOnlyList<RelayBlobDto>> PullAsync(
        string relayUrl, Guid spaceId, string token, long sinceSeq, Guid excludeDeviceId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            Url(relayUrl, $"relay/{spaceId}/changes?since={sinceSeq}&excludeDevice={excludeDeviceId}"));
        request.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<RelayBlobDto>>(ct).ConfigureAwait(false)
            ?? new List<RelayBlobDto>();
    }

    private static string Url(string relayUrl, string path) => $"{relayUrl.TrimEnd('/')}/{path}";

    private sealed record SpaceCreated(Guid SpaceId, string Token);
    private sealed record PushBody(Guid DeviceId, byte[] Payload);
    private sealed record Pushed(long Seq);
}
