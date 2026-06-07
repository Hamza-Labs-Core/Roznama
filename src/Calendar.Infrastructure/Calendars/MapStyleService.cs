using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Host-side resolution of the Map view basemap (ARCHITECTURE.md §7, geo-tiles-plugin.md §2, §10). Unlike
/// <c>geo.geocode</c>/<c>geo.route</c> this is <strong>not</strong> aggregated: a basemap is a single chosen
/// surface, so we take the first running <see cref="ITileProvider"/> from the registry and return its
/// <see cref="StyleDescriptor"/>. Every failure mode degrades to the bundled default style rather than
/// throwing: no provider, a faulted instance, or a provider that throws from <c>GetStyleAsync</c> all yield a
/// valid renderable style so the Map view never shows a blank canvas.
/// </summary>
public sealed class MapStyleService : IMapStyleService
{
    private readonly IPluginRegistry _registry;
    private readonly ILogger<MapStyleService> _logger;

    public MapStyleService(IPluginRegistry registry, ILogger<MapStyleService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<StyleDescriptor> GetStyleAsync(CancellationToken ct)
    {
        // geo.tiles is a single chosen basemap — take the first running provider (selection UI would pin one;
        // for now first-registered wins). No provider ⇒ bundled default so the map still renders.
        var registration = _registry
            .ForCapability(Capability.GeoTiles)
            .FirstOrDefault(r => r.Instance.Plugin is ITileProvider);

        if (registration?.Instance.Plugin is ITileProvider provider)
        {
            try
            {
                var style = await provider.GetStyleAsync(ct).ConfigureAwait(false);
                if (IsRenderable(style))
                    return style;
                _logger.LogWarning(
                    "geo.tiles provider {PluginId} returned an empty style; using bundled default.",
                    registration.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "geo.tiles provider {PluginId} failed to resolve a style; using bundled default.",
                    registration.Id);
            }
        }

        return BundledDefaultStyle.Descriptor;
    }

    /// <summary>A style is renderable only if it names exactly one of StyleUrl / StyleJson (mutually exclusive).</summary>
    private static bool IsRenderable(StyleDescriptor style) =>
        !string.IsNullOrWhiteSpace(style.StyleUrl) ^ !string.IsNullOrWhiteSpace(style.StyleJson);
}
