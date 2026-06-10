using Calendar.Application.Plugins;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Calendar.Infrastructure.Tests;

/// <summary>
/// Covers <see cref="MapStyleService"/> — the host-side resolution of the Map view basemap
/// (geo-tiles-plugin.md §2, §10). geo.tiles is a single chosen basemap (not aggregated), and every failure
/// mode degrades to the bundled default style rather than throwing.
/// </summary>
public sealed class MapStyleServiceTests
{
    private static MapStyleService NewService(IPluginRegistry registry) =>
        new(registry, NullLogger<MapStyleService>.Instance);

    [Fact]
    public async Task With_no_provider_returns_the_bundled_default_style()
    {
        var service = NewService(new PluginRegistry());

        var style = await service.GetStyleAsync(CancellationToken.None);

        // A bundled default is always renderable (inline JSON) with non-optional attribution.
        Assert.Equal(BundledDefaultStyle.Descriptor, style);
        Assert.Null(style.StyleUrl);
        Assert.False(string.IsNullOrWhiteSpace(style.StyleJson));
        Assert.False(string.IsNullOrWhiteSpace(style.Attribution));
    }

    [Fact]
    public async Task Returns_the_running_tile_providers_style()
    {
        var expected = new StyleDescriptor(
            StyleUrl: "https://tiles.example.test/style.json",
            StyleJson: null,
            Attribution: "© Example",
            TileKind: "vector",
            SupportsOffline: false);

        var registry = new PluginRegistry();
        registry.Register(RunningTiles(new FakeTileProvider(_ => Task.FromResult(expected))));

        var style = await NewService(registry).GetStyleAsync(CancellationToken.None);

        Assert.Equal(expected, style);
    }

    [Fact]
    public async Task A_provider_that_throws_falls_back_to_the_bundled_default()
    {
        var registry = new PluginRegistry();
        registry.Register(RunningTiles(
            new FakeTileProvider(_ => throw new InvalidOperationException("style host unreachable"))));

        var style = await NewService(registry).GetStyleAsync(CancellationToken.None);

        Assert.Equal(BundledDefaultStyle.Descriptor, style);
    }

    [Fact]
    public async Task A_provider_returning_an_empty_style_falls_back_to_the_bundled_default()
    {
        // Neither StyleUrl nor StyleJson set ⇒ not renderable ⇒ fallback (the descriptors are mutually exclusive).
        var empty = new StyleDescriptor(null, null, "© Nothing", "vector", false);
        var registry = new PluginRegistry();
        registry.Register(RunningTiles(new FakeTileProvider(_ => Task.FromResult(empty))));

        var style = await NewService(registry).GetStyleAsync(CancellationToken.None);

        Assert.Equal(BundledDefaultStyle.Descriptor, style);
    }

    private static PluginRegistration RunningTiles(ITileProvider provider) => new(
        Manifest: provider.Manifest,
        Capabilities: new[] { Capability.GeoTiles },
        State: PluginState.Running,
        Instance: new FakeInstance(provider));

    private sealed class FakeInstance(IPlugin plugin) : IPluginInstance
    {
        public string PluginId => plugin.Manifest.Id;
        public IPlugin? Plugin => plugin;
    }

    private sealed class FakeTileProvider(Func<CancellationToken, Task<StyleDescriptor>> resolve) : ITileProvider
    {
        public PluginManifest Manifest { get; } = new(
            Id: "test.tiles.fake",
            Name: "Fake Tiles",
            Version: "1.0.0",
            SdkVersion: "1.x",
            Kind: PluginKind.Assembly,
            Capabilities: new[] { CapabilityIds.GeoTiles },
            Publisher: null,
            Auth: new AuthSpec(AuthScheme.None),
            Network: new NetworkSpec(new[] { "tiles.example.test" }),
            Config: new ConfigSchema("""{"type":"object"}""", new List<string>()));

        public Task InitializeAsync(IPluginHost host, CancellationToken ct) => Task.CompletedTask;

        public Task<StyleDescriptor> GetStyleAsync(CancellationToken ct) => resolve(ct);
    }
}
