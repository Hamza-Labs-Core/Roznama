namespace Calendar.Application.Plugins;

/// <summary>
/// One entry in a marketplace registry index (ROADMAP Phase 6; PLUGINS.md §8). The index is a JSON document
/// at any URL: <c>{ "plugins": [ { id, name, version, description, capabilities, downloadUrl, sha256,
/// publisherName, signature } ] }</c>. <c>sha256</c> is the hex digest of the bundle zip;
/// <c>signature</c> is the publisher's detached signature (base64, ECDSA P-256 over the zip bytes).
/// </summary>
public sealed record MarketplaceEntry(
    string Id,
    string Name,
    string Version,
    string? Description,
    IReadOnlyList<string> Capabilities,
    string DownloadUrl,
    string? Sha256,
    string? PublisherName,
    string? Signature);

/// <summary>An install request: a direct bundle URL plus the integrity/provenance fields from the index.</summary>
public sealed record InstallPluginRequest(
    string DownloadUrl,
    string? Sha256,
    string? Signature,
    string? PublisherName);

/// <summary>The installed plugin as the registry now sees it.</summary>
public sealed record InstalledPluginDto(
    string Id,
    string Name,
    string Version,
    string State,
    string TrustTier,
    string? FaultReason);

/// <summary>
/// The plugin marketplace (ROADMAP Phase 6): fetch a registry index, install signed bundles from a URL
/// (integrity-checked, signature-verified against the trusted publisher keyset, trust-tier gated), and
/// uninstall. Unsigned bundles load only when the host explicitly opts into local-dev mode; out-of-process
/// sandboxing for untrusted code is the deferred half of ADR-0007.
/// </summary>
public interface IMarketplaceService
{
    /// <summary>Fetch and parse a registry index.</summary>
    Task<IReadOnlyList<MarketplaceEntry>> GetIndexAsync(string registryUrl, CancellationToken ct);

    /// <summary>
    /// Download, verify, extract, and hot-load a bundle. Throws <see cref="InvalidOperationException"/> on
    /// integrity/signature/trust failures (nothing is written before verification passes).
    /// </summary>
    Task<InstalledPluginDto> InstallAsync(InstallPluginRequest request, CancellationToken ct);

    /// <summary>
    /// Unload + delete an installed bundle. False when unknown; throws <see cref="InvalidOperationException"/>
    /// while accounts still use the plugin.
    /// </summary>
    Task<bool> UninstallAsync(string pluginId, CancellationToken ct);
}
