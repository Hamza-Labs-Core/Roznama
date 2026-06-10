using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Calendar.Application.Plugins;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.TrivialTest;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// The plugin marketplace (ROADMAP Phase 6; PLUGINS.md §8): a signed bundle installs from a URL and goes
/// Running with Signed trust; integrity tampering, unsigned bundles (without local-dev), and zip-slip
/// entries are all refused before anything touches disk; uninstall unloads + cleans up and refuses while
/// accounts still use the plugin.
/// </summary>
public sealed class MarketplaceTests : IDisposable
{
    private const string BundleUrl = "https://registry.test/bundles/trivial.zip";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "calendar-market-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public MarketplaceTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "market.db");
    }

    [Fact]
    public async Task A_signed_bundle_installs_hot_and_runs_with_signed_trust()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = BuildTrivialBundleZip();
        var harness = Harness.Create(_root, _dbPath, zip, TrustedKey(ecdsa));

        var installed = await harness.Marketplace.InstallAsync(new InstallPluginRequest(
            BundleUrl,
            Sha256: Convert.ToHexStringLower(SHA256.HashData(zip)),
            Signature: Convert.ToBase64String(ecdsa.SignData(zip, HashAlgorithmName.SHA256)),
            PublisherName: "Trusted Co"), CancellationToken.None);

        Assert.Equal(TrivialTilePlugin.PluginId, installed.Id);
        Assert.Equal(nameof(PluginState.Running), installed.State);
        Assert.Equal(nameof(TrustTier.Signed), installed.TrustTier);

        // Hot-loaded: the registry answers a capability query with the new plugin right away.
        var reg = Assert.Single(harness.Registry.ForCapability(Capability.GeoTiles));
        Assert.Equal(TrivialTilePlugin.PluginId, reg.Id);

        // The DB row records the provenance tier.
        var row = await harness.Db.Plugins.SingleAsync(p => p.Id == installed.Id);
        Assert.Equal(TrustTier.Signed, row.TrustTier);
    }

    [Fact]
    public async Task A_tampered_download_fails_the_integrity_gate()
    {
        var zip = BuildTrivialBundleZip();
        var harness = Harness.Create(_root, _dbPath, zip);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Marketplace.InstallAsync(new InstallPluginRequest(
                BundleUrl, Sha256: Convert.ToHexStringLower(SHA256.HashData(new byte[] { 9, 9, 9 })),
                Signature: null, PublisherName: null), CancellationToken.None));

        Assert.Contains("integrity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.Registry.All());                       // nothing was extracted or loaded.
    }

    [Fact]
    public async Task An_unsigned_bundle_is_refused_unless_local_dev_is_explicitly_allowed()
    {
        var zip = BuildTrivialBundleZip();

        var strict = Harness.Create(_root, _dbPath, zip);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            strict.Marketplace.InstallAsync(new InstallPluginRequest(BundleUrl, null, null, null), CancellationToken.None));
        Assert.Contains("trusted publisher", ex.Message);
        Assert.Empty(strict.Registry.All());

        var localDev = Harness.Create(_root, Path.Combine(_root, "localdev.db"), zip, allowUnsigned: true);
        var installed = await localDev.Marketplace.InstallAsync(
            new InstallPluginRequest(BundleUrl, null, null, null), CancellationToken.None);
        Assert.Equal(nameof(TrustTier.LocalDev), installed.TrustTier);
        Assert.Equal(nameof(PluginState.Running), installed.State);
    }

    [Fact]
    public async Task A_wrong_key_signature_does_not_grant_signed_trust()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = BuildTrivialBundleZip();
        var harness = Harness.Create(_root, _dbPath, zip, TrustedKey(trusted));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Marketplace.InstallAsync(new InstallPluginRequest(
                BundleUrl, null,
                Signature: Convert.ToBase64String(attacker.SignData(zip, HashAlgorithmName.SHA256)),
                PublisherName: "Trusted Co"), CancellationToken.None));
    }

    [Fact]
    public async Task A_zip_slip_entry_is_rejected()
    {
        using var slip = new MemoryStream();
        using (var archive = new ZipArchive(slip, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "plugin.yaml", TrivialManifestYaml());
            WriteEntry(archive, "../escape.txt", "outside!");
        }
        var harness = Harness.Create(_root, _dbPath, slip.ToArray(), allowUnsigned: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Marketplace.InstallAsync(new InstallPluginRequest(BundleUrl, null, null, null), CancellationToken.None));
        Assert.Contains("escapes", ex.Message);
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Uninstall_unloads_the_plugin_but_refuses_while_accounts_use_it()
    {
        var zip = BuildTrivialBundleZip();
        var harness = Harness.Create(_root, _dbPath, zip, allowUnsigned: true);
        var installed = await harness.Marketplace.InstallAsync(
            new InstallPluginRequest(BundleUrl, null, null, null), CancellationToken.None);

        // An account bound to the plugin blocks uninstall.
        harness.Db.Accounts.Add(new Account
        {
            Id = Guid.CreateVersion7(), PluginId = installed.Id, DisplayName = "User",
            UpdatedAtUtc = DateTimeOffset.UtcNow, DeviceId = Guid.Empty, Lamport = 1,
        });
        await harness.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Marketplace.UninstallAsync(installed.Id, CancellationToken.None));

        // Disconnect, then uninstall succeeds and the registry forgets the plugin.
        harness.Db.Accounts.RemoveRange(harness.Db.Accounts);
        await harness.Db.SaveChangesAsync();
        Assert.True(await harness.Marketplace.UninstallAsync(installed.Id, CancellationToken.None));
        Assert.False(harness.Registry.TryGet(installed.Id, out _));
        Assert.Empty(harness.Registry.ForCapability(Capability.GeoTiles));
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private sealed record Harness(
        IMarketplaceService Marketplace, IPluginRegistry Registry, CalendarDbContext Db)
    {
        public static Harness Create(
            string root, string dbPath, byte[] bundleZip,
            TrustedPublisher? trustedPublisher = null, bool allowUnsigned = false)
        {
            var db = new CalendarDbContext(
                new DbContextOptionsBuilder<CalendarDbContext>().UseSqlite($"Data Source={dbPath}").Options);
            db.Database.Migrate();

            var installDir = Path.Combine(root, "plugins");
            var hostOptions = Options.Create(new PluginHostOptions { Directories = { installDir } });
            var registry = new PluginRegistry();
            var host = new PluginHost(
                new YamlManifestReader(), new ManifestValidator(), new AssemblyPluginLoader(),
                new ConnectorEngine(), registry, NullLoggerFactory.Instance, hostOptions);

            var options = Options.Create(new MarketplaceOptions
            {
                AllowUnsigned = allowUnsigned,
                TrustedPublishers = trustedPublisher is null
                    ? new List<TrustedPublisher>()
                    : new List<TrustedPublisher> { trustedPublisher },
            });

            var marketplace = new MarketplaceService(
                host, registry, db, new HttpClient(new MapHandler(BundleUrl, bundleZip)),
                options, hostOptions, NullLogger<MarketplaceService>.Instance);
            return new Harness(marketplace, registry, db);
        }
    }

    private static TrustedPublisher TrustedKey(ECDsa key) => new()
    {
        Name = "Trusted Co",
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
    };

    private static byte[] BuildTrivialBundleZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "plugin.yaml", TrivialManifestYaml());
            var dll = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Calendar.Plugin.TrivialTest.dll"));
            using var entry = archive.CreateEntry("Calendar.Plugin.TrivialTest.dll").Open();
            entry.Write(dll);
        }
        return stream.ToArray();
    }

    private static string TrivialManifestYaml() => $$"""
        id: {{TrivialTilePlugin.PluginId}}
        name: Trivial Test Tiles
        version: 1.0.0
        sdkVersion: "1.x"
        kind: assembly
        assembly: Calendar.Plugin.TrivialTest.dll
        capabilities:
          - geo.tiles
        auth:
          scheme: none
        network:
          allow:
            - tiles.example.test
        config:
          jsonSchema: |
            {"type":"object"}
          required: []
        """;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly string _url;
        private readonly byte[] _bytes;
        public MapHandler(string url, byte[] bytes) => (_url, _bytes) = (url, bytes);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.ToString() == _url
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The collectible ALC may still map extracted DLLs until process exit — best-effort cleanup.
        }
    }
}
