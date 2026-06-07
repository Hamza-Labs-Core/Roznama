using Calendar.Application.Auth;
using Calendar.Plugin.Abstractions;
using Calendar.Infrastructure.Plugins;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Auth;

/// <summary>
/// Builds a real <see cref="IAuthBroker"/> bound to one account's <see cref="CredentialContext"/>
/// (PLUGIN-HOST.md §6). The host calls this when constructing a plugin's <c>IPluginHost</c>; the plugin still
/// only ever sees <see cref="IAuthBroker"/>. <see cref="AuthScheme.None"/> is served by the cheap
/// <see cref="NoopAuthBroker"/> so no vault/token machinery spins up for public feeds.
/// </summary>
public sealed class AuthBrokerFactory : IAuthBrokerFactory
{
    private readonly ITokenVault _vault;
    private readonly Func<HttpClient> _tokenClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _clock;

    public AuthBrokerFactory(
        ITokenVault vault,
        Func<HttpClient> tokenClientFactory,
        ILoggerFactory loggerFactory,
        TimeProvider? clock = null)
    {
        _vault = vault;
        _tokenClientFactory = tokenClientFactory;
        _loggerFactory = loggerFactory;
        _clock = clock ?? TimeProvider.System;
    }

    public IAuthBroker Create(CredentialContext context)
    {
        if (context.Spec.Scheme == AuthScheme.None)
            return new NoopAuthBroker();

        return new AuthBroker(
            context,
            _vault,
            _tokenClientFactory,
            _loggerFactory.CreateLogger($"AuthBroker.{context.AccountId:N}"),
            _clock);
    }
}
