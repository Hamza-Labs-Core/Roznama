using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The host's last-resort basemap (geo-tiles-plugin.md §10): used when no <c>geo.tiles</c> provider is loaded
/// or the selected one fails to resolve a style. A minimal, key-free MapLibre style over the serverless
/// MapLibre demo tileset so <c>GET /api/map/style</c> always returns something renderable and the Map view
/// never shows a blank canvas. This duplicates the plugin's embedded default deliberately — the host must be
/// able to answer even with zero tiles plugins installed.
/// </summary>
public static class BundledDefaultStyle
{
    /// <summary>Default attribution for OSM-derived tiles — non-optional (geo-tiles-plugin.md §9).</summary>
    public const string Attribution = "© OpenStreetMap contributors";

    /// <summary>Inline minimal MapLibre style (demo vector tileset; no key, GitHub-Pages hosted).</summary>
    public const string StyleJson = """
        {
          "version": 8,
          "name": "Unified Calendar — host default",
          "sources": {
            "maplibre-demo": {
              "type": "vector",
              "url": "https://demotiles.maplibre.org/tiles/tiles.json"
            }
          },
          "glyphs": "https://demotiles.maplibre.org/font/{fontstack}/{range}.pbf",
          "layers": [
            { "id": "background", "type": "background", "paint": { "background-color": "#d8e6f0" } },
            {
              "id": "countries-fill", "type": "fill", "source": "maplibre-demo",
              "source-layer": "countries",
              "paint": { "fill-color": "#f4f4ee", "fill-outline-color": "#b6c2cc" }
            },
            {
              "id": "countries-boundary", "type": "line", "source": "maplibre-demo",
              "source-layer": "countries",
              "paint": { "line-color": "#9aa7b1", "line-width": 1 }
            }
          ]
        }
        """;

    /// <summary>The resolved fallback descriptor handed to the browser when no provider style is available.</summary>
    public static readonly StyleDescriptor Descriptor = new(
        StyleUrl: null,
        StyleJson: StyleJson,
        Attribution: Attribution,
        TileKind: "vector",
        SupportsOffline: false);
}
