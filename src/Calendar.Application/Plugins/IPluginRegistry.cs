using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Plugins;

/// <summary>
/// The single query surface between the domain/aggregator and the plugins (PLUGIN-HOST.md §2.2). Keyed on the
/// <see cref="Capability"/> enum — never on a provider id. <c>GET /capabilities</c> and <c>GET /plugins</c>
/// are thin projections of it.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>
    /// All <see cref="PluginState.Running"/> plugins implementing <paramref name="capability"/>. The ONLY
    /// query the domain/aggregator use. Returns an empty list when none are available.
    /// </summary>
    IReadOnlyList<PluginRegistration> ForCapability(Capability capability);

    /// <summary>Host-internal lookup by plugin id (config, lifecycle), including non-running registrations.</summary>
    bool TryGet(string pluginId, out PluginRegistration registration);

    /// <summary>Every registration regardless of state — backs <c>GET /plugins</c>.</summary>
    IReadOnlyList<PluginRegistration> All();

    /// <summary>Capability → running plugin ids projection — backs <c>GET /capabilities</c>.</summary>
    IReadOnlyDictionary<Capability, IReadOnlyList<string>> Snapshot();

    /// <summary>Add or atomically replace a registration (hot-swap safe).</summary>
    void Register(PluginRegistration registration);

    /// <summary>Remove a registration on unload/upgrade.</summary>
    void Remove(string pluginId);
}
