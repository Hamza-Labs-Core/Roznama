using Calendar.Application.Auth;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Orchestrates the plugin lifecycle for Phase 0: discover → validate → load → bind → initialize → register
/// (PLUGIN-HOST.md §2.3, §3). Every gate fails closed per plugin — a bad bundle is registered
/// <see cref="PluginState.Faulted"/> with a reason and never aborts the host or other plugins.
/// </summary>
public sealed class PluginHost
{
    private readonly YamlManifestReader _manifestReader;
    private readonly ManifestValidator _validator;
    private readonly AssemblyPluginLoader _assemblyLoader;
    private readonly ConnectorEngine _connectorEngine;
    private readonly IPluginRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginHost> _logger;
    private readonly PluginHostOptions _options;
    private readonly IAuthBrokerFactory _authBrokerFactory;
    private readonly Func<PluginManifest, HttpClient>? _clientFactory;

    public PluginHost(
        YamlManifestReader manifestReader,
        ManifestValidator validator,
        AssemblyPluginLoader assemblyLoader,
        ConnectorEngine connectorEngine,
        IPluginRegistry registry,
        ILoggerFactory loggerFactory,
        IOptions<PluginHostOptions> options,
        IAuthBrokerFactory? authBrokerFactory = null,
        Func<PluginManifest, HttpClient>? clientFactory = null)
    {
        _manifestReader = manifestReader;
        _validator = validator;
        _assemblyLoader = assemblyLoader;
        _connectorEngine = connectorEngine;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginHost>();
        _options = options.Value;
        // Until an account binds a credential, a plugin is initialized with a scheme-only broker (None →
        // Noop). The full per-account broker is wired when an ACCOUNT is bound (PLUGIN-HOST.md §2.3, §6).
        _authBrokerFactory = authBrokerFactory ?? new NoopAuthBrokerFactory();
        // Tests and the connector engine inject the egress-filtered client here; the default is a plain client
        // until the EgressAllowlistHandler + Polly pipeline are wired (PLUGIN-HOST.md §7.1).
        _clientFactory = clientFactory;
    }

    /// <summary>Run discovery across all configured directories and register every bundle (or its fault).</summary>
    public async Task LoadAllAsync(CancellationToken ct)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in _options.Directories)
        {
            if (!Directory.Exists(directory))
            {
                _logger.LogDebug("Plugin directory {Directory} does not exist; skipping.", directory);
                continue;
            }

            foreach (var bundleDir in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                await LoadBundleAsync(bundleDir, seenIds, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task LoadBundleAsync(string bundleDir, HashSet<string> seenIds, CancellationToken ct)
    {
        var manifestPath = Path.Combine(bundleDir, "plugin.yaml");
        if (!File.Exists(manifestPath))
            return; // not a bundle directory

        ManifestParseResult parsed;
        try
        {
            var yaml = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            parsed = _manifestReader.Parse(yaml);
        }
        catch (ManifestFormatException ex)
        {
            _logger.LogWarning("Plugin at {Path} faulted: bad-manifest — {Detail}", manifestPath, ex.Message);
            return; // cannot register without a manifest id
        }

        var manifest = parsed.Manifest;

        // Earlier directories win on id collisions — an in-box id cannot be shadowed.
        if (!seenIds.Add(manifest.Id))
        {
            _logger.LogWarning("Duplicate plugin id {Id} at {Path}; ignoring the shadowing bundle.",
                manifest.Id, bundleDir);
            return;
        }

        var validation = _validator.Validate(manifest);
        if (!validation.Ok)
        {
            RegisterFault(manifest, validation.Capabilities, validation.FaultReason!, validation.FaultDetail!);
            return;
        }

        var bundle = new PluginBundle(
            bundleDir,
            manifestPath,
            manifest,
            parsed.AssemblyFile is { } dll ? Path.Combine(bundleDir, dll) : null,
            parsed.OpenApiFile is { } api ? Path.Combine(bundleDir, api) : null,
            parsed.Operations);

        try
        {
            var instance = manifest.Kind == PluginKind.Assembly
                ? _assemblyLoader.Load(bundle)
                : _connectorEngine.Load(bundle);

            // §3.5 capability binding — the loaded plugin must implement every declared capability interface.
            if (instance.Plugin is { } plugin)
            {
                foreach (var capability in validation.Capabilities)
                {
                    if (!CapabilityInterfaces.IsImplementedBy(capability, plugin))
                    {
                        RegisterFault(manifest, validation.Capabilities, "capability-error",
                            $"declares {CapabilityIds.For(capability)} but does not implement {CapabilityInterfaces.For(capability).Name}");
                        return;
                    }
                }

                await InitializeAsync(manifest, plugin, ct).ConfigureAwait(false);
            }

            _registry.Register(new PluginRegistration(
                manifest, validation.Capabilities, PluginState.Running, instance));
            _logger.LogInformation("Loaded plugin {Id} v{Version} ({Caps}).",
                manifest.Id, manifest.Version, string.Join(", ", manifest.Capabilities));
        }
        catch (Exception ex)
        {
            RegisterFault(manifest, validation.Capabilities, "load-error", ex.Message);
            _logger.LogWarning(ex, "Plugin {Id} faulted during load.", manifest.Id);
        }
    }

    private async Task InitializeAsync(PluginManifest manifest, IPlugin plugin, CancellationToken ct)
    {
        // One client per plugin, captured by the closure so it lives as long as the plugin (no per-call leak).
        var client = _clientFactory?.Invoke(manifest) ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        });

        // At load time no account is bound yet, so the broker is built from the manifest AuthSpec with no
        // stored credential. For AuthScheme.None this is the NoopAuthBroker; for credentialed schemes the
        // host re-binds a per-account broker once an ACCOUNT supplies its AuthRef (PLUGIN-HOST.md §6.1).
        var broker = _authBrokerFactory.Create(new CredentialContext(Guid.Empty, manifest.Auth));

        var host = new PluginHostServices(
            _loggerFactory.CreateLogger($"Plugin.{manifest.Id}"),
            broker,
            new InMemoryPluginCache(),
            () => client,
            configJson: "{}");

        await plugin.InitializeAsync(host, ct).ConfigureAwait(false);
    }

    private void RegisterFault(
        PluginManifest manifest, IReadOnlyList<Capability> capabilities, string reason, string detail)
    {
        _registry.Register(new PluginRegistration(
            manifest, capabilities, PluginState.Faulted,
            new FaultedInstance(manifest.Id), $"{reason}: {detail}"));
        _logger.LogWarning("Plugin {Id} faulted: {Reason} — {Detail}", manifest.Id, reason, detail);
    }

    private sealed class FaultedInstance(string pluginId) : IPluginInstance
    {
        public string PluginId { get; } = pluginId;
        public IPlugin? Plugin => null;
    }
}
