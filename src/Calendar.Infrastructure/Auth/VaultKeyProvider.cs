using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// Supplies the 256-bit master key that wraps every vault entry (PLUGIN-HOST.md §6.4). This is the seam where
/// a real OS keystore (Windows DPAPI / macOS Keychain / a KMS-wrapped data key) plugs in; for the Phase
/// implementation the key comes from configuration (base64) or is derived deterministically per-device.
/// The returned key never leaves the host process and is the reason the cloud relay never sees tokens.
/// </summary>
public interface IVaultKeyProvider
{
    /// <summary>The wrapping-key id stamped into <c>SecretRef.KeyId</c> so a rotated key can be identified.</summary>
    string KeyId { get; }

    /// <summary>The 32-byte AES key for <paramref name="keyId"/> (the current key when null). Throws if unknown.</summary>
    ReadOnlyMemory<byte> GetKey(string? keyId = null);
}

/// <summary>Options for <see cref="ConfigurationVaultKeyProvider"/> (bound from the <c>TokenVault</c> config section).</summary>
public sealed class VaultKeyOptions
{
    /// <summary>Base64-encoded 32-byte master key. When unset, a deterministic dev key is derived (NOT for production).</summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>Identifier recorded on each sealed entry; lets a future key rotation tell entries apart.</summary>
    public string KeyId { get; set; } = "vault-key-v1";
}

/// <summary>
/// A <see cref="IVaultKeyProvider"/> sourced from configuration. In production the master key is injected via
/// a protected configuration provider (user-secrets / environment / OS keystore-backed). When no key is
/// supplied it derives a stable 32-byte key from the key id so dev/test runs work offline — clearly NOT a
/// security boundary, which is why <see cref="IsEphemeralDevKey"/> is surfaced for an audit warning.
/// </summary>
public sealed class ConfigurationVaultKeyProvider : IVaultKeyProvider
{
    private readonly byte[] _key;

    public ConfigurationVaultKeyProvider(IOptions<VaultKeyOptions> options)
    {
        var opts = options.Value;
        KeyId = string.IsNullOrWhiteSpace(opts.KeyId) ? "vault-key-v1" : opts.KeyId;

        if (!string.IsNullOrWhiteSpace(opts.MasterKeyBase64))
        {
            _key = Convert.FromBase64String(opts.MasterKeyBase64);
            if (_key.Length != 32)
                throw new InvalidOperationException(
                    $"TokenVault master key must be 32 bytes (256-bit AES); got {_key.Length}.");
            IsEphemeralDevKey = false;
        }
        else
        {
            // Deterministic per-key-id dev key. Offline-friendly; NOT a production secret.
            _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"calendar-dev-vault::{KeyId}"));
            IsEphemeralDevKey = true;
        }
    }

    public string KeyId { get; }

    /// <summary>True when running on the derived dev key (no real master key configured) — host audits this.</summary>
    public bool IsEphemeralDevKey { get; }

    public ReadOnlyMemory<byte> GetKey(string? keyId = null)
    {
        if (keyId is not null && !string.Equals(keyId, KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unknown vault key id '{keyId}'.");
        return _key;
    }
}
