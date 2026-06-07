using System.Reflection;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.MapLibre;

/// <summary>
/// The first-party <c>geo.tiles</c> provider (ARCHITECTURE.md §7, geo-tiles-plugin.md §2). Resolves the Map
/// view's basemap to a MapLibre <see cref="StyleDescriptor"/> that the browser renders via MapLibre GL JS over
/// JS interop. A <c>geo.tiles</c> plugin contributes almost no runtime code — it is the config carrier; the
/// renderer lives in the browser.
/// <para>
/// When no <c>styleUrl</c> is configured this returns the <strong>bundled default style</strong> as inline
/// <see cref="StyleDescriptor.StyleJson"/> (the §10 fallback): a minimal MapLibre style over the serverless
/// MapLibre demo tileset, key-free and self-contained so the Map view always renders. A self-hosted /
/// keyed deployment supplies <c>styleUrl</c> (TileServer GL / PMTiles / MapTiler) and that wins instead.
/// </para>
/// </summary>
public sealed class MapLibreTilePlugin : ITileProvider
{
    /// <summary>The plugin id (reverse-DNS). Public so tests/host can reference it without a magic string.</summary>
    public const string PluginId = "org.unifiedcalendar.tiles.maplibre";

    /// <summary>Default attribution for OSM-derived tiles — non-optional (geo-tiles-plugin.md §9).</summary>
    public const string DefaultAttribution = "© OpenStreetMap contributors";

    private IPluginHost? _host;

    public PluginManifest Manifest { get; } = new(
        Id: PluginId,
        Name: "MapLibre / OSM tiles",
        Version: "1.0.0",
        SdkVersion: "1.x",
        Kind: PluginKind.Assembly,
        Capabilities: new[] { CapabilityIds.GeoTiles },
        Publisher: new PluginPublisher("Unified Calendar", Signature: null),
        Auth: new AuthSpec(AuthScheme.None),
        Network: new NetworkSpec(new[] { "demotiles.maplibre.org" }),
        Config: new ConfigSchema(
            """{"type":"object","properties":{"styleUrl":{"type":"string"},"attribution":{"type":"string"}}}""",
            new List<string>()));

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        return Task.CompletedTask;
    }

    public Task<StyleDescriptor> GetStyleAsync(CancellationToken ct)
    {
        var config = TryGetConfig();

        var attribution = string.IsNullOrWhiteSpace(config?.Attribution)
            ? DefaultAttribution
            : config!.Attribution!;

        // A configured styleUrl (self-hosted TileServer GL / PMTiles / keyed provider) wins; the host's auth
        // broker would inject any {apiKey} for keyed schemes. With none configured we fall back to the bundled
        // default inline style (geo-tiles-plugin.md §10) so the basemap always renders.
        if (!string.IsNullOrWhiteSpace(config?.StyleUrl))
        {
            return Task.FromResult(new StyleDescriptor(
                StyleUrl: config!.StyleUrl,
                StyleJson: null,
                Attribution: attribution,
                TileKind: "vector",
                SupportsOffline: false));
        }

        return Task.FromResult(new StyleDescriptor(
            StyleUrl: null,
            StyleJson: BundledDefaultStyleJson.Value,
            Attribution: attribution,
            TileKind: "vector",
            // The bundled default points at the public MapLibre demo tileset — do NOT precache (the demo
            // service is best-effort, like the public OSM policy in §5). Self-hosted styles flip this on.
            SupportsOffline: false));
    }

    private TilesConfig? TryGetConfig()
    {
        if (_host is null)
            return null;
        try
        {
            return _host.GetConfig<TilesConfig>();
        }
        catch
        {
            // No account config bound (the Map view resolves the style with no per-account settings) → defaults.
            return null;
        }
    }

    /// <summary>
    /// Lazily-loaded bundled default MapLibre style (embedded resource <c>default-style.json</c>). Read once
    /// per process; the same inline style is handed to every Map view mount.
    /// </summary>
    private static class BundledDefaultStyleJson
    {
        internal static readonly string Value = Load();

        private static string Load()
        {
            var asm = typeof(MapLibreTilePlugin).Assembly;
            var name = Array.Find(
                asm.GetManifestResourceNames(),
                n => n.EndsWith("default-style.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    "Bundled default MapLibre style (default-style.json) is missing from the plugin assembly.");

            using var stream = asm.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded resource '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>Bound from <c>plugin.yaml</c> config — both optional (geo-tiles-plugin.md §8).</summary>
internal sealed record TilesConfig(string? StyleUrl, string? Attribution);
