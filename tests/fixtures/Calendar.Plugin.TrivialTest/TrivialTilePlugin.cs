using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.TrivialTest;

/// <summary>
/// The smallest real plugin: a single-capability <c>geo.tiles</c> provider used to prove the host can
/// discover, validate, load (into a collectible ALC), bind a capability, initialize, and register a plugin —
/// the Phase 0 "registry can load a trivial plugin" acceptance check.
/// </summary>
public sealed class TrivialTilePlugin : ITileProvider
{
    public const string PluginId = "org.unifiedcalendar.test.trivial";

    public PluginManifest Manifest { get; } = new(
        Id: PluginId,
        Name: "Trivial Test Tiles",
        Version: "1.0.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.GeoTiles },
        Publisher: new PluginPublisher("Unified Calendar Tests", Signature: null),
        Auth: new AuthSpec(AuthScheme.None),
        Network: new NetworkSpec(new[] { "tiles.example.test" }),
        Config: new ConfigSchema("""{"type":"object"}""", new List<string>()));

    /// <summary>Set true once <see cref="InitializeAsync"/> runs — lets the test assert the lifecycle reached it.</summary>
    public bool Initialized { get; private set; }

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        Initialized = true;
        return Task.CompletedTask;
    }

    public Task<StyleDescriptor> GetStyleAsync(CancellationToken ct) =>
        Task.FromResult(new StyleDescriptor(
            StyleUrl: "https://tiles.example.test/style.json",
            StyleJson: null,
            Attribution: "© Unified Calendar Tests",
            TileKind: "vector",
            SupportsOffline: false));
}
