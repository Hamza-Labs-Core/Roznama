using System.Reflection;
using System.Runtime.Loader;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// One collectible <see cref="AssemblyLoadContext"/> per assembly plugin (PLUGIN-HOST.md §4.1). Gives
/// dependency isolation (plugins may ship different versions of their private deps) and unloadability.
///
/// The critical rule is the single shared contract boundary: <c>Calendar.Plugin.Abstractions</c> (and the
/// logging-abstractions it depends on) are NOT loaded into the plugin's ALC — the resolver defers them to the
/// default context so <c>IPlugin</c>, <c>PluginManifest</c>, <c>AuthHandle</c>, etc. have the SAME
/// <see cref="Type"/> identity on both sides. Duplicating the contract per-ALC would make every cast fail.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    private static readonly HashSet<string> Shared = new(StringComparer.Ordinal)
    {
        "Calendar.Plugin.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        // the BCL is shared implicitly via the default context's TPA list
    };

    public PluginLoadContext(string mainDllPath)
        : base(name: Path.GetFileNameWithoutExtension(mainDllPath), isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(mainDllPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && Shared.Contains(name))
            return null; // → fall back to the Default ALC (shared type identity)

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
