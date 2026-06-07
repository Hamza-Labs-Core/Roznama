using System.Security.Cryptography;
using System.Text;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// PKCE primitives (RFC 7636) used by the OAuth connect flow (PLUGIN-HOST.md §6.3). The host mints a
/// high-entropy <c>code_verifier</c>, derives the S256 <c>code_challenge</c>, and a CSRF <c>state</c> — none
/// of which a plugin ever sees.
/// </summary>
internal static class Pkce
{
    /// <summary>A 32-byte (256-bit) base64url verifier — within RFC 7636's 43–128 char range.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>An opaque, CSRF-binding state token.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge: base64url(SHA256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
