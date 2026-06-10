using Calendar.Domain;

namespace Calendar.Application.Auth;

/// <summary>
/// The encrypted token vault that backs the auth broker (PLUGIN-HOST.md §6.4, SDK-CONTRACT.md §3). It is the
/// single place OAuth refresh/access tokens, API keys, and Basic/app passwords live as ciphertext. Tokens are
/// device-local by policy — never E2E-synced (ARCHITECTURE §8: "tokens stay on the authorizing device").
/// Backed by the <c>SecretRef</c> table; entries are AEAD-sealed (AES-GCM) by the infrastructure implementation.
/// </summary>
public interface ITokenVault
{
    /// <summary>
    /// Seal <paramref name="value"/> under <paramref name="kind"/> and persist it, returning the vault handle
    /// (a <c>SecretRef.Id</c>). <paramref name="aad"/> is bound into the AEAD (e.g. accountId + kind) so a
    /// ciphertext can't be swapped between rows. <paramref name="expiresAtUtc"/> records access-token expiry.
    /// </summary>
    Task<Guid> StoreAsync(SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct);

    /// <summary>
    /// Overwrite an existing vault entry in place (used by silent refresh so the handle id stays stable across
    /// token rotation). The same <paramref name="aad"/> must be supplied or the open fails.
    /// </summary>
    Task UpdateAsync(Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct);

    /// <summary>Open and decrypt a vault entry, or null if it does not exist. Throws if the AEAD tag fails.</summary>
    Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct);

    /// <summary>Purge a vault entry (account disconnect / token revoke — PLUGIN-HOST.md §6.3).</summary>
    Task RemoveAsync(Guid id, CancellationToken ct);
}

/// <summary>A decrypted vault entry. The plaintext <see cref="Value"/> never leaves the host process.</summary>
public sealed record VaultEntry(SecretKind Kind, string Value, DateTimeOffset? ExpiresAtUtc);
