using System.Text.Json;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// A parsed OAuth 2.0 token-endpoint response (RFC 6749 §5.1). The broker reads <c>access_token</c>,
/// <c>expires_in</c>, and an optionally-rotated <c>refresh_token</c>; everything else (token_type, scope) is
/// ignored. <see cref="ExpiresAt"/> is computed from <c>expires_in</c> against the time of the response.
/// </summary>
internal sealed record OAuthToken(string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt)
{
    public static OAuthToken Parse(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        string? refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;

        DateTimeOffset? expiresAt = null;
        if (root.TryGetProperty("expires_in", out var e))
        {
            // expires_in may arrive as a JSON number or a numeric string depending on the provider.
            long seconds = e.ValueKind == JsonValueKind.Number
                ? e.GetInt64()
                : (long.TryParse(e.GetString(), out var s) ? s : 0);
            if (seconds > 0)
                expiresAt = now.AddSeconds(seconds);
        }

        return new OAuthToken(access, refresh, expiresAt);
    }
}
