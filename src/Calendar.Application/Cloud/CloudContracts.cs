namespace Calendar.Application.Cloud;

/// <summary>This device's cloud-sync state for the UI/API (<c>GET /cloud/status</c>).</summary>
public sealed record CloudStatus(
    bool Enabled,
    string? RelayUrl,
    Guid? SpaceId,
    long LastPulledSeq,
    DateTimeOffset? LastSyncAtUtc);

/// <summary>
/// Result of enabling cloud sync. <see cref="SpaceToken"/> is set only when a NEW space was created — the
/// user copies it (plus the passphrase) to other devices; it is never retrievable again.
/// </summary>
public sealed record CloudEnableResult(Guid SpaceId, string? SpaceToken);

/// <summary>One sync round: local changes pushed, encrypted blobs pulled, rows applied after LWW.</summary>
public sealed record CloudSyncSummary(int Pushed, int BlobsPulled, int Applied);

/// <summary>
/// The optional E2E-encrypted cloud sync engine (ROADMAP Phase 6, ADR-0003). The key is derived from the
/// user's passphrase client-side; the relay stores only ciphertext blobs. Synced: the user-state
/// <c>ISyncEntity</c> set (accounts sans credentials, calendars, categories, places, shares, fare watches).
/// Provider event caches re-sync per device; tokens NEVER leave the authorizing device.
/// </summary>
public interface ICloudSyncService
{
    /// <summary>
    /// Enroll this device: derive the key from <paramref name="passphrase"/>, create a relay space (or join
    /// <paramref name="spaceId"/> with <paramref name="spaceToken"/>), vault the key + token.
    /// </summary>
    Task<CloudEnableResult> EnableAsync(
        string passphrase, string relayUrl, Guid? spaceId, string? spaceToken, CancellationToken ct);

    /// <summary>Drop the enrollment + vaulted key/token. Local data stays; the relay keeps its (unreadable) blobs.</summary>
    Task DisableAsync(CancellationToken ct);

    Task<CloudStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Push this device's changes, then pull + apply other devices' (LWW by Lamport, then UpdatedAtUtc).</summary>
    Task<CloudSyncSummary> SyncAsync(CancellationToken ct);
}

/// <summary>One encrypted blob fetched from a relay.</summary>
public sealed record RelayBlobDto(long Seq, Guid DeviceId, byte[] Payload);

/// <summary>The device-side HTTP client for any relay node (the API below is also self-hosted by every host).</summary>
public interface IRelayClient
{
    Task<(Guid SpaceId, string Token)> CreateSpaceAsync(string relayUrl, CancellationToken ct);

    Task<long> PushAsync(
        string relayUrl, Guid spaceId, string token, Guid deviceId, byte[] payload, CancellationToken ct);

    Task<IReadOnlyList<RelayBlobDto>> PullAsync(
        string relayUrl, Guid spaceId, string token, long sinceSeq, Guid excludeDeviceId, CancellationToken ct);
}

/// <summary>
/// The server side of a relay node (backs the <c>/relay/*</c> endpoints): append-ordered opaque blobs per
/// space, token-gated. Null results mean unknown space / bad token (the endpoint answers 404/401).
/// </summary>
public interface IRelayStore
{
    Task<(Guid SpaceId, string Token)> CreateSpaceAsync(CancellationToken ct);

    Task<long?> AppendAsync(Guid spaceId, string token, Guid deviceId, byte[] payload, CancellationToken ct);

    Task<IReadOnlyList<RelayBlobDto>?> ReadAsync(
        Guid spaceId, string token, long sinceSeq, Guid? excludeDeviceId, CancellationToken ct);
}
