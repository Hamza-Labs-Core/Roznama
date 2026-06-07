using System.Reflection;
using System.Runtime.Loader;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// In-process assembly-plugin handle: holds the collectible ALC and the constructed <see cref="IPlugin"/>
/// so the host can later drop the instance and unload the context (PLUGIN-HOST.md §4.2, §4.4).
/// </summary>
public sealed class InProcPluginInstance : IPluginInstance
{
    private AssemblyLoadContext? _alc;

    internal InProcPluginInstance(string pluginId, AssemblyLoadContext alc, IPlugin plugin)
    {
        PluginId = pluginId;
        _alc = alc;
        Plugin = plugin;
    }

    public string PluginId { get; }
    public IPlugin? Plugin { get; private set; }

    /// <summary>
    /// Drop the strong references the host holds and unload the ALC, returning a weak reference to verify
    /// collection (PLUGIN-HOST.md §4.4). Best-effort: collectible unload is cooperative.
    /// </summary>
    public WeakReference UnloadAlc()
    {
        var alc = _alc;
        Plugin = null;
        _alc = null;
        var weak = new WeakReference(alc);
        alc?.Unload();
        return weak;
    }
}

/// <summary>
/// Loads an assembly plugin into its own collectible ALC, finds the single <see cref="IPlugin"/> type, and
/// constructs it (PLUGIN-HOST.md §4.2). <see cref="IPlugin.InitializeAsync"/> is NOT called here — it runs at
/// configure time once config/auth are bound.
/// </summary>
public sealed class AssemblyPluginLoader
{
    public InProcPluginInstance Load(PluginBundle bundle)
    {
        if (bundle.MainDllPath is null)
            throw new PluginLoadException($"assembly plugin '{bundle.Manifest.Id}' has no main DLL.");
        if (!File.Exists(bundle.MainDllPath))
            throw new PluginLoadException($"assembly DLL not found: {bundle.MainDllPath}");

        var alc = new PluginLoadContext(bundle.MainDllPath);
        var assembly = alc.LoadFromAssemblyPath(bundle.MainDllPath);

        var pluginTypes = assembly.GetTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .ToArray();

        if (pluginTypes.Length == 0)
            throw new PluginLoadException($"no IPlugin implementation found in '{bundle.MainDllPath}'.");
        if (pluginTypes.Length > 1)
            throw new PluginLoadException(
                $"multiple IPlugin implementations in '{bundle.MainDllPath}': {string.Join(", ", pluginTypes.Select(t => t.FullName))}.");

        IPlugin plugin;
        try
        {
            plugin = (IPlugin)Activator.CreateInstance(pluginTypes[0])!;
        }
        catch (Exception ex)
        {
            throw new PluginLoadException(
                $"failed to construct '{pluginTypes[0].FullName}': {ex.Message}", ex);
        }

        // Anti-spoof: the plugin's self-reported manifest id must match the bundle's manifest id.
        if (!string.Equals(plugin.Manifest.Id, bundle.Manifest.Id, StringComparison.Ordinal))
            throw new PluginLoadException(
                $"manifest id mismatch: bundle '{bundle.Manifest.Id}' vs assembly '{plugin.Manifest.Id}'.");

        return new InProcPluginInstance(bundle.Manifest.Id, alc, plugin);
    }
}

/// <summary>Thrown when an assembly plugin cannot be loaded or constructed (→ a faulted registration).</summary>
public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string message, Exception? inner = null) : base(message, inner) { }
}
