using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>Outcome of manifest validation. <see cref="FaultReason"/> is null on success.</summary>
public sealed record ManifestValidationResult(
    bool Ok,
    string? FaultReason,
    string? FaultDetail,
    IReadOnlyList<Capability> Capabilities)
{
    public static ManifestValidationResult Success(IReadOnlyList<Capability> capabilities) =>
        new(true, null, null, capabilities);

    public static ManifestValidationResult Fault(string reason, string detail) =>
        new(false, reason, detail, Array.Empty<Capability>());
}

/// <summary>
/// The fail-closed validation gates that run before load (PLUGIN-HOST.md §3.2, §3.5): SDK-version
/// compatibility and capability-id membership. Signature/permission gates arrive with later phases; for now
/// an empty/missing network allowlist is permitted at validation and enforced as deny-all at call time.
/// </summary>
public sealed class ManifestValidator
{
    public ManifestValidationResult Validate(PluginManifest manifest)
    {
        // §3.2 — sdkVersion must admit the host's current contract version.
        bool compatible;
        try
        {
            compatible = SdkVersion.IsCompatible(manifest.SdkVersion);
        }
        catch (FormatException ex)
        {
            return ManifestValidationResult.Fault("bad-manifest",
                $"unparseable sdkVersion '{manifest.SdkVersion}': {ex.Message}");
        }

        if (!compatible)
            return ManifestValidationResult.Fault("sdk-mismatch",
                $"plugin requires SDK {manifest.SdkVersion}; host is {SdkVersion.Current}");

        // §3.5 — every declared capability id must exist in the catalog.
        if (manifest.Capabilities.Count == 0)
            return ManifestValidationResult.Fault("capability-error", "plugin declares no capabilities");

        var capabilities = new List<Capability>(manifest.Capabilities.Count);
        foreach (var id in manifest.Capabilities)
        {
            if (!CapabilityIds.TryParse(id, out var capability))
                return ManifestValidationResult.Fault("capability-error", $"unknown capability id '{id}'");
            capabilities.Add(capability);
        }

        return ManifestValidationResult.Success(capabilities);
    }
}
