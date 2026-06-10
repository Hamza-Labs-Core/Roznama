using System.Text;
using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Phase 1 secret vault (DATA-SCHEMA §2.2). Stores the value as bytes in a <see cref="SecretRef"/> row; the
/// AEAD + SQLCipher layers arrive in a later phase. Kept behind <see cref="ISecretVault"/> so swapping in real
/// encryption never touches callers.
/// </summary>
public sealed class SecretVault : ISecretVault
{
    private readonly CalendarDbContext _db;

    public SecretVault(CalendarDbContext db) => _db = db;

    public async Task<Guid> StoreAsync(SecretKind kind, string value, CancellationToken ct)
    {
        var secret = new SecretRef
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            KeyId = "phase1-plaintext",
            Algorithm = "none",
            Nonce = Array.Empty<byte>(),
            Ciphertext = Encoding.UTF8.GetBytes(value),
            RotatedAtUtc = DateTimeOffset.UtcNow,
            RowVersion = Guid.NewGuid().ToString("N"),
        };
        _db.Secrets.Add(secret);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return secret.Id;
    }

    public async Task<string?> ReadAsync(Guid id, CancellationToken ct)
    {
        var secret = await _db.Secrets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        return secret is null ? null : Encoding.UTF8.GetString(secret.Ciphertext);
    }
}
