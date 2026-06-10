using System.Security.Cryptography;
using System.Text;
using Calendar.Application.Cloud;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Cloud;

/// <summary>
/// The server side of the relay (ADR-0003): append-ordered, token-gated, OPAQUE blob storage per space.
/// Every host doubles as a relay node — one instance can serve as another's cloud. The space token is
/// stored hashed; the blobs are E2E ciphertext the relay can order but never read.
/// </summary>
public sealed class RelayStore : IRelayStore
{
    private readonly CalendarDbContext _db;

    public RelayStore(CalendarDbContext db) => _db = db;

    public async Task<(Guid SpaceId, string Token)> CreateSpaceAsync(CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var space = new RelaySpace
        {
            Id = Guid.CreateVersion7(),
            TokenHash = Hash(token),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _db.RelaySpaces.Add(space);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (space.Id, token);
    }

    public async Task<long?> AppendAsync(
        Guid spaceId, string token, Guid deviceId, byte[] payload, CancellationToken ct)
    {
        if (!await AuthorizedAsync(spaceId, token, ct).ConfigureAwait(false))
            return null;

        var blob = new RelayBlob
        {
            SpaceId = spaceId,
            DeviceId = deviceId,
            Payload = payload,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _db.RelayBlobs.Add(blob);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return blob.Seq;
    }

    public async Task<IReadOnlyList<RelayBlobDto>?> ReadAsync(
        Guid spaceId, string token, long sinceSeq, Guid? excludeDeviceId, CancellationToken ct)
    {
        if (!await AuthorizedAsync(spaceId, token, ct).ConfigureAwait(false))
            return null;

        return await _db.RelayBlobs
            .Where(b => b.SpaceId == spaceId && b.Seq > sinceSeq
                        && (excludeDeviceId == null || b.DeviceId != excludeDeviceId))
            .OrderBy(b => b.Seq)
            .Select(b => new RelayBlobDto(b.Seq, b.DeviceId, b.Payload))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> AuthorizedAsync(Guid spaceId, string token, CancellationToken ct)
    {
        var space = await _db.RelaySpaces.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spaceId, ct).ConfigureAwait(false);
        return space is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(space.TokenHash), Encoding.ASCII.GetBytes(Hash(token)));
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
