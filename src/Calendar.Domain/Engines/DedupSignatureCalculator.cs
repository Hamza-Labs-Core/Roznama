using System.Security.Cryptography;
using System.Text;

namespace Calendar.Domain.Engines;

/// <summary>
/// Computes the deterministic, reversible dedup signature for an event
/// (ARCHITECTURE §12): <c>hash(normalizedTitle | startDate | allDay | endDate?)</c>. Two events from
/// different calendars with the same signature collapse into one visible entry. Pure and side-effect free.
/// </summary>
public static class DedupSignatureCalculator
{
    /// <summary>
    /// Compute the signature for the given event fields. <paramref name="endDate"/> participates only when
    /// supplied (holidays/birthdays are single-day and typically omit it). The result is a lower-case hex
    /// SHA-256 string, stable across process runs and machine architectures.
    /// </summary>
    public static string Compute(string? title, DateOnly startDate, bool allDay, DateOnly? endDate = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var payload = string.Create(
            CultureInfoInvariant,
            $"{normalizedTitle}|{startDate:yyyy-MM-dd}|{(allDay ? 1 : 0)}|{(endDate is { } e ? e.ToString("yyyy-MM-dd", CultureInfoInvariant) : string.Empty)}");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Normalize a title for signature comparison: lower-cased, emoji/symbols/punctuation stripped,
    /// whitespace runs collapsed to a single space, trimmed. Iterates over <see cref="Rune"/>s so multi-byte
    /// emoji are handled as single units rather than mangled surrogate halves.
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var sb = new StringBuilder(title.Length);
        var pendingSpace = false;

        foreach (var rune in title.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            // Keep letters and digits only; punctuation, symbols, and emoji are dropped.
            if (!Rune.IsLetterOrDigit(rune))
                continue;

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        return sb.ToString();
    }

    private static readonly System.Globalization.CultureInfo CultureInfoInvariant =
        System.Globalization.CultureInfo.InvariantCulture;
}
