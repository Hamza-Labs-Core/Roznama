using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Auth;

/// <summary>
/// Constructs an <see cref="IAuthBroker"/> bound to one account's <see cref="CredentialContext"/>
/// (PLUGIN-HOST.md §6). The host calls this when building a plugin's <c>IPluginHost</c>, so the plugin
/// receives a real broker that runs its scheme — while still only ever seeing the <see cref="IAuthBroker"/>
/// surface. <see cref="AuthScheme.None"/> can be served by a no-op broker (no context required).
/// </summary>
public interface IAuthBrokerFactory
{
    /// <summary>
    /// Build a broker for <paramref name="context"/>. The returned broker reads/writes the account's tokens
    /// through the vault and mints scoped, short-lived <see cref="AuthHandle"/>s; it never exposes secrets.
    /// </summary>
    IAuthBroker Create(CredentialContext context);
}
