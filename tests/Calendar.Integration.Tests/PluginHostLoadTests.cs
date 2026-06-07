using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Calendar.Plugin.TrivialTest;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Phase 0 acceptance: the plugin host can discover → validate → load (collectible ALC) → bind capability →
/// initialize → register a real bundle, and the registry answers a capability query with it
/// (PLUGIN-HOST.md §2.3, §11).
/// </summary>
public sealed class PluginHostLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calendar-plugin-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Registry_loads_a_trivial_plugin_and_answers_a_capability_query()
    {
        var registry = await BuildAndLoadAsync(WriteTrivialBundle());

        var tilers = registry.ForCapability(Capability.GeoTiles);

        var reg = Assert.Single(tilers);
        Assert.Equal(TrivialTilePlugin.PluginId, reg.Id);
        Assert.Equal(PluginState.Running, reg.State);

        var plugin = Assert.IsAssignableFrom<ITileProvider>(reg.Instance.Plugin);
        var style = await plugin.GetStyleAsync(CancellationToken.None);
        Assert.Equal("https://tiles.example.test/style.json", style.StyleUrl);
    }

    [Fact]
    public async Task An_sdk_mismatched_plugin_is_faulted_not_loaded()
    {
        var bundle = WriteTrivialBundle(sdkVersion: "2.x");
        var registry = await BuildAndLoadAsync(bundle);

        Assert.Empty(registry.ForCapability(Capability.GeoTiles));
        Assert.True(registry.TryGet(TrivialTilePlugin.PluginId, out var reg));
        Assert.Equal(PluginState.Faulted, reg.State);
        Assert.Contains("sdk-mismatch", reg.FaultReason);
    }

    [Fact]
    public async Task A_bad_manifest_is_skipped_without_aborting_discovery()
    {
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "plugin.yaml"), "id: only-an-id\n# missing required fields");
        WriteTrivialBundle();

        var registry = await BuildAndLoadAsync(_root);

        // The good plugin still loaded despite the broken sibling bundle.
        Assert.Single(registry.ForCapability(Capability.GeoTiles));
    }

    private string WriteTrivialBundle(string sdkVersion = "1.x")
    {
        var bundleDir = Path.Combine(_root, "trivial");
        Directory.CreateDirectory(bundleDir);

        var sourceDll = Path.Combine(AppContext.BaseDirectory, "Calendar.Plugin.TrivialTest.dll");
        File.Copy(sourceDll, Path.Combine(bundleDir, "Calendar.Plugin.TrivialTest.dll"), overwrite: true);

        File.WriteAllText(Path.Combine(bundleDir, "plugin.yaml"), $$"""
            id: {{TrivialTilePlugin.PluginId}}
            name: Trivial Test Tiles
            version: 1.0.0
            sdkVersion: "{{sdkVersion}}"
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
            """);

        return _root;
    }

    private static async Task<IPluginRegistry> BuildAndLoadAsync(string directory)
    {
        var registry = new PluginRegistry();
        var host = new PluginHost(
            new YamlManifestReader(),
            new ManifestValidator(),
            new AssemblyPluginLoader(),
            new ConnectorEngine(),
            registry,
            NullLoggerFactory.Instance,
            Options.Create(new PluginHostOptions { Directories = { directory } }));

        await host.LoadAllAsync(CancellationToken.None);
        return registry;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The collectible ALC may still map the copied DLL until process exit — best-effort cleanup.
        }
    }
}
