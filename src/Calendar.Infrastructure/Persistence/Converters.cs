using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Calendar.Infrastructure.Persistence;

/// <summary>
/// <see cref="DateTimeOffset"/> ↔ UTC ISO-8601 text (DATA-SCHEMA §5.1). Every instant is normalized to UTC
/// and stored as round-trip text so SQLite's lexicographic ordering equals chronological ordering — which the
/// range scans in DATA-SCHEMA §4 depend on.
/// </summary>
public sealed class DateTimeOffsetToUtcStringConverter : ValueConverter<DateTimeOffset, string>
{
    public DateTimeOffsetToUtcStringConverter()
        : base(
            v => v.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffK", CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal))
    {
    }
}

/// <summary><see cref="Guid"/> ↔ lower-case 36-char <c>D</c> text (DATA-SCHEMA §1, §5.1) — TEXT, never BLOB.</summary>
public sealed class GuidToStringConverter : ValueConverter<Guid, string>
{
    public GuidToStringConverter()
        : base(
            v => v.ToString("D", CultureInfo.InvariantCulture),
            v => Guid.Parse(v))
    {
    }
}
