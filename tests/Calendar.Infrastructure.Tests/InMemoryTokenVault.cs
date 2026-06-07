using Calendar.Application.Auth;
using Calendar.Domain;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// A minimal in-memory <see cref="ITokenVault"/> for broker tests that don't need to exercise AES-GCM
/// persistence (the encryption itself is covered by <c>AesGcmTokenVaultTests</c>). It still enforces the AAD
/// binding so the broker's account-scoping is verified.
/// </summary>
public sealed class InMemoryTokenVault : ITokenVault
{
    private readonly Dictionary<Guid, Row> _rows = new();

    public Task<Guid> StoreAsync(
        SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        _rows[id] = new Row(kind, value, aad, expiresAtUtc);
        return Task.FromResult(id);
    }

    public Task UpdateAsync(
        Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        _rows[id] = new Row(kind, value, aad, expiresAtUtc);
        return Task.CompletedTask;
    }

    public Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct)
    {
        if (!_rows.TryGetValue(id, out var row))
            return Task.FromResult<VaultEntry?>(null);
        if (row.Aad != aad)
            throw new InvalidOperationException("AAD mismatch — ciphertext bound to a different account.");
        return Task.FromResult<VaultEntry?>(new VaultEntry(row.Kind, row.Value, row.ExpiresAtUtc));
    }

    public Task RemoveAsync(Guid id, CancellationToken ct)
    {
        _rows.Remove(id);
        return Task.CompletedTask;
    }

    /// <summary>Test seam: pre-seed a row and return its handle (as the connect flow would).</summary>
    public Guid Seed(SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc = null)
    {
        var id = Guid.CreateVersion7();
        _rows[id] = new Row(kind, value, aad, expiresAtUtc);
        return id;
    }

    /// <summary>Test seam: read the raw stored value (to assert a rotated refresh token was re-vaulted).</summary>
    public string? Raw(Guid id) => _rows.TryGetValue(id, out var row) ? row.Value : null;

    private readonly record struct Row(SecretKind Kind, string Value, string? Aad, DateTimeOffset? ExpiresAtUtc);
}
