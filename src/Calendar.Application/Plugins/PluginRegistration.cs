using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Plugins;

/// <summary>
/// Where a plugin is in its lifecycle (PLUGIN-HOST.md §2.2). Only <see cref="Running"/> registrations are
/// dispatched to; a <see cref="Faulted"/> or not-yet-running plugin is filtered out of capability queries.
/// </summary>
public enum PluginState
{
    Discovered,
    Validated,
    Loaded,
    Configured,
    Running,
    Faulted,
    Unloaded
}

/// <summary>
/// A loaded plugin instance the host can invoke. Wraps the in-process <see cref="IPlugin"/> today; the same
/// abstraction will front an out-of-process gRPC proxy for untrusted plugins (PLUGIN-HOST.md §8.4).
/// </summary>
public interface IPluginInstance
{
    /// <summary>The plugin id (reverse-DNS), available even when <see cref="Plugin"/> failed to construct.</summary>
    string PluginId { get; }

    /// <summary>The underlying plugin, or null for a faulted registration that never instantiated.</summary>
    IPlugin? Plugin { get; }
}

/// <summary>
/// One entry in the capability registry (PLUGIN-HOST.md §2.2). Immutable; lifecycle transitions produce a
/// new record so in-flight <see cref="IPluginRegistry.ForCapability"/> enumerations are never torn.
/// </summary>
public sealed record PluginRegistration(
    PluginManifest Manifest,
    IReadOnlyList<Capability> Capabilities,
    PluginState State,
    IPluginInstance Instance,
    string? FaultReason = null)
{
    /// <summary>Convenience: the plugin id from the manifest.</summary>
    public string Id => Manifest.Id;
}
