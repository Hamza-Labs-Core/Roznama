using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Calendars;

/// <summary>
/// Resolves the Map view's basemap (ARCHITECTURE.md §7, geo-tiles-plugin.md §2). <c>geo.tiles</c> is a
/// <strong>single chosen basemap</strong>, not an aggregated capability: this picks the selected running
/// <see cref="ITileProvider"/> and returns its <see cref="StyleDescriptor"/>. With no provider registered (or
/// one that faults) it returns a bundled default style so the Map view always renders — it never throws.
/// </summary>
public interface IMapStyleService
{
    /// <summary>The resolved basemap style — the selected provider's, or a bundled default fallback.</summary>
    Task<StyleDescriptor> GetStyleAsync(CancellationToken ct);
}
