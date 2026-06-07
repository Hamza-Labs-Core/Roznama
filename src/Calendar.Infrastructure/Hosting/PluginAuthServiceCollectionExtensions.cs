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

        // The vault writes to the per-request CalendarDbContext; broker/flow read tokens through ITokenVault.
        services.AddScoped<ITokenVault, AesGcmTokenVault>();

        // A dedicated short-lived HttpClient for token-endpoint calls (decompression on); not egress-filtered
        // because the broker only ever talks to the provider's configured AuthSpec.TokenUrl.
        services.AddSingleton<Func<HttpClient>>(_ => () => new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        }));

        services.AddScoped<IAuthBrokerFactory, AuthBrokerFactory>();
        services.AddScoped<IOAuthFlowService, OAuthFlowService>();

        return services;
    }
}
