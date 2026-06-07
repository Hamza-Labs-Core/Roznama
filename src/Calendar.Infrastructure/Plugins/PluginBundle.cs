using Calendar.Plugin.Abstractions;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// A discovered plugin package on disk (PLUGIN-HOST.md §3.1): its directory, the parsed manifest, and — for
/// assembly plugins — the path to the main DLL. Declarative connectors carry an OpenAPI document instead.
/// </summary>
public sealed record PluginBundle(
    string Directory,
    string ManifestPath,
    PluginManifest Manifest,
    string? MainDllPath,
    string? OpenApiPath);
