namespace Calendar.Plugin.Abstractions;

/// <summary>
/// The SDK contract version this build of <c>Calendar.Plugin.Abstractions</c> exposes.
/// Compared against each plugin manifest's <see cref="PluginManifest.SdkVersion"/> at load time.
/// </summary>
public static class SdkVersion
{
    /// <summary>Current contract version (semver). Bumped per the policy in SDK-CONTRACT.md §1.</summary>
    public const string Current = "1.0.0";

    /// <summary>Current major. A plugin requesting a different major is rejected at load.</summary>
    public const int Major = 1;

    /// <summary>
    /// True if a plugin built against <paramref name="requested"/> (e.g. <c>"1.x"</c>, <c>"1.2"</c>,
    /// <c>"1.2.0"</c>) is loadable against this SDK: same major, and requested minor ≤ current minor
    /// (additive-only guarantees forward source-compat within the major).
    /// </summary>
    public static bool IsCompatible(string requested) =>
        SdkVersionRange.Parse(requested).Allows(Current);
}

/// <summary>Parsed semver compatibility range from a manifest's <c>sdkVersion</c> field.</summary>
public readonly record struct SdkVersionRange(int Major, int? Minor)
{
    /// <summary>Parses <c>"1.x"</c> (major-only), <c>"1.2"</c> (major+minor), or <c>"1.2.0"</c> (exact → major+minor).</summary>
    public static SdkVersionRange Parse(string range) => SdkVersionRangeParser.Parse(range);

    /// <summary>True if <paramref name="version"/> satisfies this range (same major; minor ≥ requested when pinned).</summary>
    public bool Allows(string version) => SdkVersionRangeParser.Allows(this, version);
}

/// <summary>
/// Parsing/comparison helpers behind <see cref="SdkVersionRange"/>. The grammar a manifest may use for its
/// <c>sdkVersion</c> field is intentionally tiny — major-only (<c>"1.x"</c>/<c>"1.*"</c>), major+minor
/// (<c>"1.2"</c>), or an exact three-part version (<c>"1.2.0"</c>, patch ignored for range purposes).
/// </summary>
internal static class SdkVersionRangeParser
{
    public static SdkVersionRange Parse(string range)
    {
        if (string.IsNullOrWhiteSpace(range))
            throw new FormatException("sdkVersion range must be non-empty (e.g. \"1.x\", \"1.2\", \"1.2.0\").");

        var parts = range.Trim().Split('.');
        if (parts.Length is < 1 or > 3)
            throw new FormatException($"Unrecognized sdkVersion range '{range}'.");

        if (!int.TryParse(parts[0], out var major) || major < 0)
            throw new FormatException($"Invalid major in sdkVersion range '{range}'.");

        // Major-only: "1", "1.x", "1.*".
        if (parts.Length == 1)
            return new SdkVersionRange(major, null);

        if (IsWildcard(parts[1]))
            return new SdkVersionRange(major, null);

        if (!int.TryParse(parts[1], out var minor) || minor < 0)
            throw new FormatException($"Invalid minor in sdkVersion range '{range}'.");

        // "1.2" or "1.2.0" — the patch (parts[2]) is not part of the range, only validated if present.
        if (parts.Length == 3 && !IsWildcard(parts[2]) && (!int.TryParse(parts[2], out var patch) || patch < 0))
            throw new FormatException($"Invalid patch in sdkVersion range '{range}'.");

        return new SdkVersionRange(major, minor);
    }

    public static bool Allows(SdkVersionRange range, string version)
    {
        var (vMajor, vMinor) = ParseVersion(version);
        if (range.Major != vMajor)
            return false;
        // Additive-only within a major: a plugin pinned to minor M loads against current minor ≥ M.
        return range.Minor is not { } requestedMinor || requestedMinor <= vMinor;
    }

    private static (int Major, int Minor) ParseVersion(string version)
    {
        var parts = version.Trim().Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            throw new FormatException($"Invalid semver '{version}'.");
        return (major, minor);
    }

    private static bool IsWildcard(string part) =>
        part is "x" or "X" or "*";
}
