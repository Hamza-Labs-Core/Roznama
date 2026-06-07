using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Hosting;

/// <summary>Composes the plugin host into the DI container (PLUGIN-HOST.md §2.1).</summary>
public static class PluginHostServiceCollectionExtensions
{
    /// <summary>
    /// Register the Phase 0 plugin host: registry, manifest reader/validator, the collectible-ALC loader,
    /// the connector-engine stub, the orchestrator, and the boot hosted service. Binds
    /// <see cref="PluginHostOptions"/> from the <c>PluginHost</c> configuration section.
    /// </summary>
    public static IServiceCollection AddPluginHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PluginHostOptions>(configuration.GetSection("PluginHost"));

        services.AddSingleton<YamlManifestReader>();
        services.AddSingleton<ManifestValidator>();
        services.AddSingleton<AssemblyPluginLoader>();
        services.AddSingleton<ConnectorEngine>();
        services.AddSingleton<IPluginRegistry, PluginRegistry>();
        services.AddSingleton<PluginHost>();

        services.AddHostedService<PluginHostBootstrap>();
        return services;
    }
}
