using Calendar.Application.Plugins;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Stub of the declarative connector engine (PLUGIN-HOST.md §5). The full engine parses OpenAPI, binds
/// capability methods to operations, and synthesizes an <c>IPlugin</c> from a manifest + JSONata mapping.
/// Phase 0 ships only the assembly-plugin path, so a declarative bundle is registered as faulted with a
/// clear reason rather than silently dropped. This is the seam the real engine plugs into.
/// </summary>
public sealed class ConnectorEngine
{
    /// <summary>Whether the engine can currently materialize declarative connectors.</summary>
    public bool IsImplemented => false;

    public IPluginInstance Load(PluginBundle bundle) =>
        throw new NotSupportedException(
            "The declarative connector engine is not implemented yet (Phase 0 stub). " +
            $"Plugin '{bundle.Manifest.Id}' must ship as an assembly plugin for now.");
}
