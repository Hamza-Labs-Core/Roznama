using Calendar.Application.Auth;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Loads every first-party plugin's REAL built bundle through the host (manifest parse → ALC load → capability
/// binding → initialize → register). This catches integration breakage that per-plugin unit tests cannot — a
/// missing <c>assembly:</c> manifest field, an unbundled NuGet dependency, a credentialed scheme that faults at
/// load — by asserting each plugin reaches <see cref="PluginState.Running"/>.
/// </summary>
public sealed class FirstPartyPluginLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "calendar-firstparty", Guid.NewGuid().ToString("N"));

    private static readonly (string Project, string Id)[] Plugins =
    {
        ("Calendar.Plugin.Ics", "org.unifiedcalendar.ics"),
        ("Calendar.Plugin.CalDav", "org.unifiedcalendar.caldav"),
        ("Calendar.Plugin.Google", "org.unifiedcalendar.google"),
        ("Calendar.Plugin.Microsoft", "org.unifiedcalendar.microsoft"),
        ("Calendar.Plugin.MapLibre", "org.unifiedcalendar.tiles.maplibre"),
        ("Calendar.Plugin.Duffel", "org.unifiedcalendar.duffel"),
    };

    [Fact]
    public async Task Every_first_party_plugin_bundle_loads_and_reaches_running()
    {
        var repoRoot = FindRepoRoot();
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";

        foreach (var (project, id) in Plugins)
        {
            var binDir = Path.Combine(repoRoot, "plugins", project, "bin", config, "net9.0");
            Assert.True(Directory.Exists(binDir), $"plugin output not found: {binDir} (build the solution first)");
            CopyBundle(binDir, Path.Combine(_root, id));
        }

        var registry = new PluginRegistry();
        var host = new PluginHost(
            new YamlManifestReader(),
            new ManifestValidator(),
            new AssemblyPluginLoader(),
            new ConnectorEngine(),
            registry,
            NullLoggerFactory.Instance,
            Options.Create(new PluginHostOptions { Directories = { _root } }),
            authBrokerFactory: new AnySchemeBrokerFactory());

        await host.LoadAllAsync(CancellationToken.None);

        foreach (var (_, id) in Plugins)
        {
            Assert.True(registry.TryGet(id, out var reg), $"plugin {id} was not registered");
            Assert.True(reg.State == PluginState.Running,
                $"plugin {id} is {reg.State}: {reg.FaultReason}");
        }

        // The MapLibre geo.tiles plugin must resolve a renderable StyleDescriptor: with no styleUrl configured
        // it returns the bundled default inline style (geo-tiles-plugin.md §10) with non-optional attribution.
        Assert.True(registry.TryGet("org.unifiedcalendar.tiles.maplibre", out var tiles));
        var provider = Assert.IsAssignableFrom<ITileProvider>(tiles.Instance.Plugin);
        var style = await provider.GetStyleAsync(CancellationToken.None);

        Assert.True(
            !string.IsNullOrWhiteSpace(style.StyleUrl) ^ !string.IsNullOrWhiteSpace(style.StyleJson),
            "exactly one of StyleUrl / StyleJson must be set");
        Assert.False(string.IsNullOrWhiteSpace(style.StyleJson), "no styleUrl configured ⇒ inline bundled default");
        Assert.Contains("OpenStreetMap", style.Attribution);
        Assert.Contains("\"version\"", style.StyleJson);
    }

    private static void CopyBundle(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            // The SDK contract is the shared ALC boundary — a real bundle never ships its own copy.
            if (name.StartsWith("Calendar.Plugin.Abstractions.", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destDir, name), overwrite: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Calendar.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Calendar.sln not found above the test base directory.");
    }

    /// <summary>A permissive broker factory: serves any scheme with a no-op handle, so load is tested independently of live auth.</summary>
    private sealed class AnySchemeBrokerFactory : IAuthBrokerFactory
    {
        public IAuthBroker Create(CredentialContext context) => new AnyBroker();

        private sealed class AnyBroker : IAuthBroker
        {
            public Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
                Task.FromResult(AuthHandle.Empty);

            public Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct) =>
                Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
