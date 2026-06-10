using System.Security.Cryptography;
using System.Text.Json;
using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Creates, lists, revokes, and resolves tokenized read-only ICS shares (ARCHITECTURE §16; API.md sharing).
/// A share targets a single calendar or a filtered union (calendars[]/categories[]) and carries an
/// unguessable high-entropy <see cref="Share.Token"/>, a <see cref="ShareScope"/>, and an optional expiry.
/// Revocation is an <c>IsDeleted</c> tombstone (DATA-SCHEMA §2.7) so the global query filter stops resolving
/// the token. Tokens are secrets: they never appear in logs (only a redacted prefix), per ARCHITECTURE §17.
/// </summary>
public sealed class ShareService : IShareService
{
    /// <summary>256 bits of entropy, URL-safe base64 → ~43 unguessable chars. Matches OAuth-token strength.</summary>
    private const int TokenBytes = 32;

    private readonly CalendarDbContext _db;
    private readonly DeviceProvider _device;
    private readonly ILogger<ShareService> _logger;

    public ShareService(CalendarDbContext db, DeviceProvider device, ILogger<ShareService> logger)
    {
        _db = db;
        _device = device;
        _logger = logger;
    }

    public async Task<ShareDto> CreateAsync(CreateShareRequest request, CancellationToken ct)
    {
        // Exactly one of {calendarId, filter} defines the share target.
        var calendarIds = ResolveCalendarIds(request);
        if (calendarIds.Count == 0)
            throw new ArgumentException("A share must target a calendar or a non-empty filter.", nameof(request));

        // Validate the referenced calendars exist (avoids minting dead-link tokens).
        var existing = await _db.Calendars
            .Where(c => calendarIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var missing = calendarIds.Except(existing).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Unknown calendar(s): {string.Join(", ", missing)}.", nameof(request));

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var share = new Share
        {
            Id = Guid.CreateVersion7(),
            CalendarId = request.Filter is null ? request.CalendarId : null,
            Filter = request.Filter is null ? null : SerializeFilter(request.Filter),
            Token = MintToken(),
            Scope = request.Scope,
            ExpiresAtUtc = request.ExpiresAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            Lamport = 1,
            IsDeleted = false,
        };

        _db.Shares.Add(share);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Created share {ShareId} scope={Scope} token={TokenPrefix}… (calendars={Count})",
            share.Id, share.Scope, Redact(share.Token), calendarIds.Count);

        return ToDto(share);
    }

    public async Task<IReadOnlyList<ShareDto>> ListAsync(CancellationToken ct)
    {
        // Global !IsDeleted filter already hides revoked shares.
        var shares = await _db.Shares
            .OrderByDescending(s => s.UpdatedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return shares.Select(ToDto).ToList();
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken ct)
    {
        var share = await _db.Shares.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (share is null)
            return false;

        share.IsDeleted = true;
        share.UpdatedAtUtc = DateTimeOffset.UtcNow;
        share.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        share.Lamport++;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Revoked share {ShareId} token={TokenPrefix}…", share.Id, Redact(share.Token));
        return true;
    }

    public async Task<ShareResolveResult> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return ShareResolveResult.NotFound;

        // Unknown OR revoked → 404 (the !IsDeleted global filter folds revoked into "unknown").
        var share = await _db.Shares
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Token == token, ct).ConfigureAwait(false);
        if (share is null)
            return ShareResolveResult.NotFound;

        if (share.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return ShareResolveResult.Expired;

        var calendarIds = await ResolveCalendarIdsAsync(share, ct).ConfigureAwait(false);
        var categoryIds = ResolveFilter(share)?.CategoryIds ?? Array.Empty<Guid>();
        var feedName = await ResolveFeedNameAsync(share, calendarIds, ct).ConfigureAwait(false);

        return ShareResolveResult.Found(new ResolvedShare(
            share.Id, share.Scope, calendarIds, categoryIds, feedName));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<Guid> ResolveCalendarIds(CreateShareRequest request)
    {
        if (request.Filter is { } filter)
            return filter.CalendarIds.Distinct().ToList();
        return request.CalendarId is { } id ? new[] { id } : Array.Empty<Guid>();
    }

    private async Task<IReadOnlyList<Guid>> ResolveCalendarIdsAsync(Share share, CancellationToken ct)
    {
        if (share.CalendarId is { } id)
            return new[] { id };

        var filter = ResolveFilter(share);
        if (filter is null || filter.CalendarIds.Count == 0)
            return Array.Empty<Guid>();

        // Only resolve calendars that still exist (a deleted calendar drops out of the union).
        var ids = filter.CalendarIds.ToHashSet();
        return await _db.Calendars
            .Where(c => ids.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveFeedNameAsync(Share share, IReadOnlyList<Guid> calendarIds, CancellationToken ct)
    {
        if (share.CalendarId is { } id)
        {
            var name = await _db.Calendars
                .Where(c => c.Id == id)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return calendarIds.Count == 1 ? "Shared calendar" : "Shared calendars";
    }

    private ShareFilter? ResolveFilter(Share share)
    {
        if (string.IsNullOrWhiteSpace(share.Filter))
            return null;
        try
        {
            var dto = JsonSerializer.Deserialize<FilterJson>(share.Filter);
            if (dto is null)
                return null;
            return new ShareFilter(
                dto.Calendars ?? Array.Empty<Guid>(),
                dto.Categories ?? Array.Empty<Guid>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeFilter(ShareFilter filter) =>
        JsonSerializer.Serialize(new FilterJson(
            filter.CalendarIds.Distinct().ToArray(),
            filter.CategoryIds.Distinct().ToArray()));

    private ShareDto ToDto(Share share)
    {
        var filter = ResolveFilter(share);
        return new ShareDto(
            share.Id,
            share.CalendarId,
            filter,
            share.Scope,
            share.Token,
            $"/share/{share.Token}.ics",
            share.ExpiresAtUtc,
            share.UpdatedAtUtc);
    }

    /// <summary>Mint an unguessable, URL-safe, high-entropy token (ARCHITECTURE §17 — a capability secret).</summary>
    private static string MintToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Redact a token for logs: only a short prefix, never the full secret.</summary>
    private static string Redact(string token) =>
        token.Length <= 6 ? "***" : token[..6];

    private sealed record FilterJson(Guid[]? Calendars, Guid[]? Categories);
}
