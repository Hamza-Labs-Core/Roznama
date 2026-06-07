using Calendar.Domain;

namespace Calendar.Application.Calendars;

/// <summary>
/// The filter that defines a union share: an explicit set of calendars, optionally narrowed to certain
/// categories (ARCHITECTURE §16). A single-calendar share is the degenerate case where exactly one
/// calendar id is present and no category filter is applied.
/// </summary>
public sealed record ShareFilter(
    IReadOnlyList<Guid> CalendarIds,
    IReadOnlyList<Guid> CategoryIds);

/// <summary>Request to publish a calendar or filtered union as a tokenized read-only feed (API.md sharing).</summary>
public sealed record CreateShareRequest(
    Guid? CalendarId,
    ShareFilter? Filter,
    ShareScope Scope,
    DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// A management-side view of a share. <see cref="Token"/> is a secret; it is returned to the owner on
/// create/list (they own the link) but must never be logged (ARCHITECTURE §16, §17).
/// </summary>
public sealed record ShareDto(
    Guid Id,
    Guid? CalendarId,
    ShareFilter? Filter,
    ShareScope Scope,
    string Token,
    string FeedPath,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>The resolved target of a public feed request, used to drive projection + serialization.</summary>
public sealed record ResolvedShare(
    Guid Id,
    ShareScope Scope,
    IReadOnlyList<Guid> CalendarIds,
    IReadOnlyList<Guid> CategoryIds,
    string FeedName);

/// <summary>Why a public token failed to resolve, mapping to the feed endpoint's HTTP status.</summary>
public enum ShareResolution
{
    /// <summary>Token resolved to a live share.</summary>
    Ok,

    /// <summary>Unknown or revoked token → 404.</summary>
    NotFound,

    /// <summary>Token is past its <c>ExpiresAtUtc</c> → 410 Gone.</summary>
    Expired,
}

/// <summary>Outcome of resolving a public token (ARCHITECTURE §16): the share, or why it failed.</summary>
public sealed record ShareResolveResult(ShareResolution Resolution, ResolvedShare? Share)
{
    public static ShareResolveResult Found(ResolvedShare share) => new(ShareResolution.Ok, share);
    public static readonly ShareResolveResult NotFound = new(ShareResolution.NotFound, null);
    public static readonly ShareResolveResult Expired = new(ShareResolution.Expired, null);
}

/// <summary>
/// Creates, lists, revokes, and resolves tokenized read-only shares (ARCHITECTURE §16; API.md sharing).
/// Tokens are unguessable high-entropy secrets — redacted in logs, surfaced only to the owning UI.
/// </summary>
public interface IShareService
{
    /// <summary>Publish a calendar or filtered union; mints an unguessable token. Returns the owner-facing view.</summary>
    Task<ShareDto> CreateAsync(CreateShareRequest request, CancellationToken ct);

    /// <summary>All active (non-revoked) shares for the owning UI.</summary>
    Task<IReadOnlyList<ShareDto>> ListAsync(CancellationToken ct);

    /// <summary>Revoke a share by id (an <c>IsDeleted</c> tombstone). False if unknown/already revoked.</summary>
    Task<bool> RevokeAsync(Guid id, CancellationToken ct);

    /// <summary>Resolve a public token to its projection target, or report not-found/expired.</summary>
    Task<ShareResolveResult> ResolveAsync(string token, CancellationToken ct);
}
