using System.Net;
using Calendar.Application.Auth;
using Calendar.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Infrastructure.Hosting;

/// <summary>
/// Registers the OAuth broker + encrypted token vault (PLUGIN-HOST.md §6, SDK-CONTRACT.md §3): the AES-GCM
/// vault, its master-key provider, the per-account broker factory, and the PKCE connect-flow service. After
/// this the host hands plugins a real <c>IAuthBroker</c> instead of a no-op.
/// </summary>
public static class PluginAuthServiceCollectionExtensions
{
    public static IServiceCollection AddPluginAuth(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<VaultKeyOptions>(cfg.GetSection("TokenVault"));

        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IVaultKeyProvider, ConfigurationVaultKeyProvider>();

        // Singleton-safe: the vault opens a DbContext scope per operation (it's held by long-lived plugin
        // brokers), so the broker factory and flow can be singletons too — and the flow MUST be a singleton
        // because it holds the in-memory pending-PKCE state across the begin/complete requests.
        services.AddSingleton<ITokenVault, AesGcmTokenVault>();

        // A dedicated short-lived HttpClient for token-endpoint calls (decompression on); not egress-filtered
        // because the broker only ever talks to the provider's configured AuthSpec.TokenUrl.
        services.AddSingleton<Func<HttpClient>>(_ => () => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        }));

        services.AddSingleton<IAuthBrokerFactory, AuthBrokerFactory>();
        services.AddSingleton<IOAuthFlowService, OAuthFlowService>();

        // Host-owned OAuth client registrations (PLUGIN-HOST.md §6.3): OAuthClients:{pluginId}:ClientId/….
        services.AddSingleton<IOAuthClientRegistry, ConfigurationOAuthClientRegistry>();

        return services;
    }
}
