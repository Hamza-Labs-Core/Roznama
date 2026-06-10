using System.Security.Cryptography;
using System.Text;
using Calendar.Application.Auth;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// The encrypted token vault (PLUGIN-HOST.md §6.4, SDK-CONTRACT.md §3). Seals every credential with AES-256-GCM
/// — a 96-bit random nonce per write, the 128-bit auth tag stored alongside, and the caller-supplied
/// associated-data (e.g. accountId + kind) bound into the AEAD so a ciphertext can't be swapped between rows.
/// Persists to the <c>SecretRef</c> table (DATA-SCHEMA §2.2); the master key comes from <see cref="IVaultKeyProvider"/>
/// and never leaves the process, so tokens stay device-local (ARCHITECTURE §8).
/// </summary>
public sealed class AesGcmTokenVault : ITokenVault
{
    private const string Algorithm = "AES-256-GCM";
    private const int NonceSize = 12; // 96-bit nonce — the AES-GCM standard
    private const int TagSize = 16;   // 128-bit auth tag

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVaultKeyProvider _keys;

    /// <summary>
    /// Takes an <see cref="IServiceScopeFactory"/> rather than a <see cref="CalendarDbContext"/> so the vault is
    /// safe as a singleton: long-lived plugin brokers hold it, and it opens a fresh DbContext scope per
    /// operation instead of capturing a scoped context.
    /// </summary>
    public AesGcmTokenVault(IServiceScopeFactory scopeFactory, IVaultKeyProvider keys)
    {
        _scopeFactory = scopeFactory;
        _keys = keys;
    }

    public async Task<Guid> StoreAsync(
        SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        var sealed_ = Seal(value, aad);
        var secret = new SecretRef
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            KeyId = _keys.KeyId,
            Algorithm = Algorithm,
            Nonce = sealed_.Nonce,
            Ciphertext = sealed_.Ciphertext,
            AuthTag = sealed_.Tag,
            Aad = aad,
            ExpiresAtUtc = expiresAtUtc,
            RotatedAtUtc = DateTimeOffset.UtcNow,
            RowVersion = Guid.NewGuid().ToString("N"),
        };
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return secret.Id;
    }

    public async Task UpdateAsync(
        Guid id, SecretKind kind, string value, string? aad, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
        var secret = await db.Secrets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Vault entry {id} does not exist.");

        var sealed_ = Seal(value, aad);
        secret.Kind = kind;
        secret.KeyId = _keys.KeyId;
        secret.Algorithm = Algorithm;
        secret.Nonce = sealed_.Nonce;
        secret.Ciphertext = sealed_.Ciphertext;
        secret.AuthTag = sealed_.Tag;
        secret.Aad = aad;
        secret.ExpiresAtUtc = expiresAtUtc;
        secret.RotatedAtUtc = DateTimeOffset.UtcNow;
        secret.RowVersion = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<VaultEntry?> ReadAsync(Guid id, string? aad, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
        var secret = await db.Secrets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (secret is null)
            return null;

        var plaintext = Open(secret, aad);
        return new VaultEntry(secret.Kind, plaintext, secret.ExpiresAtUtc);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
        var secret = await db.Secrets.FindAsync(new object?[] { id }, ct).ConfigureAwait(false);
        if (secret is null)
            return;
        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private SealedBlob Seal(string plaintext, string? aad)
    {
        var key = _keys.GetKey().Span;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];
        var aadBytes = aad is null ? null : Encoding.UTF8.GetBytes(aad);

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plainBytes, cipher, tag, aadBytes);
        return new SealedBlob(nonce, cipher, tag);
    }

    private string Open(SecretRef secret, string? aad)
    {
        if (!string.Equals(secret.Algorithm, Algorithm, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Vault entry {secret.Id} uses unsupported algorithm '{secret.Algorithm}'.");
        if (secret.AuthTag is null)
            throw new CryptographicException($"Vault entry {secret.Id} has no auth tag — cannot verify.");

        // Bind to the entry's own AAD when the caller didn't pass one, so a tampered/mismatched AAD is rejected.
        var aadBytes = (aad ?? secret.Aad) is { } a ? Encoding.UTF8.GetBytes(a) : null;
        var key = _keys.GetKey(secret.KeyId).Span;
        var plain = new byte[secret.Ciphertext.Length];

        using var gcm = new AesGcm(key, TagSize);
        // Throws CryptographicAuthenticationTagMismatchException on a tampered ciphertext/tag/AAD.
        gcm.Decrypt(secret.Nonce, secret.Ciphertext, secret.AuthTag, plain, aadBytes);
        return Encoding.UTF8.GetString(plain);
    }

    private readonly record struct SealedBlob(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
}
