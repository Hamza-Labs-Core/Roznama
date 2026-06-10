using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PluginEntity = Calendar.Domain.Entities.Plugin;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// The plugin marketplace (ROADMAP Phase 6; PLUGINS.md §8, ADR-0007): installs bundles from a URL/registry
/// with fail-closed gates, in order —
/// <list type="number">
///   <item>size cap on the download;</item>
///   <item>SHA-256 integrity check against the index digest;</item>
///   <item>detached-signature verification (ECDSA P-256 over the zip bytes) against the TRUSTED PUBLISHER
///     keyset → <see cref="TrustTier.Signed"/>; unsigned/unknown loads only when <c>AllowUnsigned</c>
///     (local-dev) is explicitly on → <see cref="TrustTier.LocalDev"/>;</item>
///   <item>zip-slip-safe extraction into the install directory;</item>
///   <item>hot-load through the same validate→bind→initialize pipeline as startup discovery.</item>
/// </list>
/// Nothing touches disk before the verification gates pass. Out-of-process sandboxing for untrusted plugins
/// is the deferred half of ADR-0007 — community bundles stay rejected rather than loaded in-proc.
/// </summary>
public sealed class MarketplaceService : IMarketplaceService
{
    private readonly PluginHost _host;
    private readonly IPluginRegistry _registry;
    private readonly CalendarDbContext _db;
    private readonly HttpClient _http;
    private readonly MarketplaceOptions _options;
    private readonly PluginHostOptions _hostOptions;
    private readonly ILogger<MarketplaceService> _logger;

    public MarketplaceService(
        PluginHost host, IPluginRegistry registry, CalendarDbContext db, HttpClient http,
        IOptions<MarketplaceOptions> options, IOptions<PluginHostOptions> hostOptions,
        ILogger<MarketplaceService> logger)
    {
        _host = host;
        _registry = registry;
        _db = db;
        _http = http;
        _options = options.Value;
        _hostOptions = hostOptions.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MarketplaceEntry>> GetIndexAsync(string registryUrl, CancellationToken ct)
    {
        var index = await _http.GetFromJsonAsync<RegistryIndex>(
            registryUrl, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct).ConfigureAwait(false);
        return index?.Plugins ?? new List<MarketplaceEntry>();
    }

    public async Task<InstalledPluginDto> InstallAsync(InstallPluginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
            throw new ArgumentException("A bundle download URL is required.", nameof(request));

        // 1. Download with a hard size cap.
        var zipBytes = await DownloadAsync(request.DownloadUrl, ct).ConfigureAwait(false);

        // 2. Integrity: the index digest must match the bytes we actually fetched.
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
            if (!actual.Equals(request.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Bundle integrity check failed: the download does not match the registry's sha256.");
        }

        // 3. Provenance: a valid detached signature from a trusted publisher ⇒ Signed; otherwise the bundle
        //    loads only in explicit local-dev mode (in-proc untrusted code is refused — ADR-0007).
        var tier = VerifySignature(zipBytes, request.Signature, request.PublisherName);
        if (tier is null)
        {
            if (!_options.AllowUnsigned)
                throw new InvalidOperationException(
                    "The bundle is not signed by a trusted publisher. Configure Marketplace:TrustedPublishers, " +
                    "or set Marketplace:AllowUnsigned=true to permit local-dev bundles.");
            tier = TrustTier.LocalDev;
            _logger.LogWarning("Installing UNSIGNED bundle from {Url} under local-dev trust.", request.DownloadUrl);
        }

        // 4. Extract (zip-slip-safe) into the install directory, named by the manifest id.
        var manifestId = ReadManifestId(zipBytes);
        var installRoot = InstallRoot();
        var bundleDir = Path.Combine(installRoot, Sanitize(manifestId));
        if (Directory.Exists(bundleDir))
        {
            _host.Unload(manifestId);           // upgrade: drop the old ALC before overwriting its files.
            Directory.Delete(bundleDir, recursive: true);
        }
        ExtractSafely(zipBytes, bundleDir);

        // 5. Hot-load through the standard pipeline (validate → bind → initialize → register).
        var registration = await _host.InstallBundleAsync(bundleDir, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The bundle has no parsable plugin.yaml manifest.");

        await UpsertPluginRowAsync(registration, tier.Value, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Installed plugin {Id} v{Version} from {Url} ({Tier}, {State}).",
            registration.Id, registration.Manifest.Version, request.DownloadUrl, tier, registration.State);
        return ToDto(registration, tier.Value);
    }

    public async Task<bool> UninstallAsync(string pluginId, CancellationToken ct)
    {
        var hasAccounts = await _db.Accounts.AnyAsync(a => a.PluginId == pluginId, ct).ConfigureAwait(false);
        if (hasAccounts)
            throw new InvalidOperationException(
                $"Plugin '{pluginId}' still has connected accounts; disconnect them first.");

        var known = _host.Unload(pluginId);

        var bundleDir = Path.Combine(InstallRoot(), Sanitize(pluginId));
        if (Directory.Exists(bundleDir))
        {
            try
            {
                Directory.Delete(bundleDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The collectible ALC unload is cooperative; the dir may stay mapped until process exit.
                _logger.LogWarning(ex, "Bundle files for {Id} could not be fully removed; retry after restart.", pluginId);
            }
            known = true;
        }

        var row = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == pluginId, ct).ConfigureAwait(false);
        if (row is not null)
        {
            _db.Plugins.Remove(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            known = true;
        }
        return known;
    }

    // ── gates ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var cap = _options.MaxBundleBytes;
        if (response.Content.Headers.ContentLength is { } length && length > cap)
            throw new InvalidOperationException($"Bundle exceeds the {cap / (1024 * 1024)} MB size cap.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
                throw new InvalidOperationException($"Bundle exceeds the {cap / (1024 * 1024)} MB size cap.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary><see cref="TrustTier.Signed"/> on a valid signature from a trusted key; null otherwise.</summary>
    private TrustTier? VerifySignature(byte[] zipBytes, string? signatureBase64, string? publisherName)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64))
            return null;

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return null;
        }

        var candidates = _options.TrustedPublishers
            .Where(p => publisherName is null || string.Equals(p.Name, publisherName, StringComparison.OrdinalIgnoreCase));
        foreach (var publisher in candidates)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publisher.PublicKey), out _);
                if (ecdsa.VerifyData(zipBytes, signature, HashAlgorithmName.SHA256))
                    return TrustTier.Signed;
            }
            catch (CryptographicException)
            {
                // a malformed configured key never blocks the others.
            }
        }
        return null;
    }

    private static string ReadManifestId(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = archive.GetEntry("plugin.yaml")
            ?? throw new InvalidOperationException("The bundle zip has no plugin.yaml at its root.");

        using var reader = new StreamReader(entry.Open());
        var manifest = new YamlManifestReader().Parse(reader.ReadToEnd());
        return manifest.Manifest.Id;
    }

    private static void ExtractSafely(byte[] zipBytes, string targetDir)
    {
        var root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);

        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;   // directory entry

            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Bundle entry '{entry.FullName}' escapes the install directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private string InstallRoot()
    {
        var root = _options.InstallDirectory
            ?? _hostOptions.Directories.FirstOrDefault()
            ?? throw new InvalidOperationException("No plugin install directory is configured.");
        Directory.CreateDirectory(root);
        return root;
    }

    private async Task UpsertPluginRowAsync(
        PluginRegistration registration, TrustTier tier, CancellationToken ct)
    {
        var manifest = registration.Manifest;
        var row = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == manifest.Id, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new PluginEntity { Id = manifest.Id, InstalledAtUtc = DateTimeOffset.UtcNow };
            _db.Plugins.Add(row);
        }
        row.Name = manifest.Name;
        row.Version = manifest.Version;
        row.SdkVersion = manifest.SdkVersion;
        row.Kind = manifest.Kind;
        row.Status = PluginStatus.Installed;
        row.Capabilities = manifest.Capabilities.ToList();
        row.Manifest = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        row.TrustTier = tier;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static string Sanitize(string pluginId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(pluginId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static InstalledPluginDto ToDto(PluginRegistration registration, TrustTier tier) => new(
        registration.Id,
        registration.Manifest.Name,
        registration.Manifest.Version,
        registration.State.ToString(),
        tier.ToString(),
        registration.FaultReason);

    private sealed record RegistryIndex(List<MarketplaceEntry> Plugins);
}

/// <summary>Marketplace knobs (bound from the <c>Marketplace</c> config section).</summary>
public sealed class MarketplaceOptions
{
    /// <summary>Publishers whose detached signatures grant <see cref="TrustTier.Signed"/> in-proc loading.</summary>
    public List<TrustedPublisher> TrustedPublishers { get; set; } = new();

    /// <summary>Explicit local-dev escape hatch: unsigned bundles install as <see cref="TrustTier.LocalDev"/>.</summary>
    public bool AllowUnsigned { get; set; }

    /// <summary>Where installed bundles land; defaults to the first plugin-host discovery directory.</summary>
    public string? InstallDirectory { get; set; }

    /// <summary>Hard cap on a bundle download (default 50 MB).</summary>
    public long MaxBundleBytes { get; set; } = 50 * 1024 * 1024;
}

/// <summary>One trusted publisher: a display name + an ECDSA P-256 public key (base64 SubjectPublicKeyInfo).</summary>
public sealed class TrustedPublisher
{
    public string Name { get; set; } = default!;
    public string PublicKey { get; set; } = default!;
}
