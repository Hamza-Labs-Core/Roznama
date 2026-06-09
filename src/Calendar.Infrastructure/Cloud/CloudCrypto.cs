using System.Security.Cryptography;
using System.Text;

namespace Calendar.Infrastructure.Cloud;

/// <summary>
/// The E2E layer of cloud sync (ADR-0003): a 256-bit AES-GCM key derived from the user's passphrase with
/// PBKDF2 (per-space salt), payloads framed as <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>. Key derivation and
/// all encryption happen on the device — a relay only ever stores the framed ciphertext.
/// </summary>
public static class CloudCrypto
{
    private const int Iterations = 210_000;   // OWASP-order PBKDF2-SHA256 work factor.
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>Derive the space key. The salt folds in the space id so equal passphrases differ per space.</summary>
    public static byte[] DeriveKey(string passphrase, Guid spaceId)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes($"unified-calendar:cloud:{spaceId:N}"));
        return Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var tag = new byte[TagBytes];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag);

        var framed = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(framed, 0);
        tag.CopyTo(framed, NonceBytes);
        cipher.CopyTo(framed, NonceBytes + TagBytes);
        return framed;
    }

    /// <summary>Throws <see cref="CryptographicException"/> on a wrong key or tampered payload.</summary>
    public static byte[] Decrypt(byte[] key, byte[] framed)
    {
        if (framed.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Payload too short to be a sealed cloud-sync blob.");

        var nonce = framed.AsSpan(0, NonceBytes);
        var tag = framed.AsSpan(NonceBytes, TagBytes);
        var cipher = framed.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
