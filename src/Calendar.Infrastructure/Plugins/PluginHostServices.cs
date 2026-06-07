using System.Collections.Concurrent;
using System.Text.Json;
using Calendar.Application.Auth;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// A minimal <see cref="IPluginHost"/> for Phase 0 (PLUGIN-HOST.md §7). It gives a plugin a logger, an
/// in-memory namespaced cache, an empty auth handle, a plain HttpClient, and its validated config JSON.
/// The egress allowlist handler, the real auth broker, the Polly resilience pipeline, and the SQLite-backed
/// cache are layered on in later phases — this is the structural shape they slot into.
/// </summary>
public sealed class PluginHostServices : IPluginHost
{
    private readonly Func<HttpClient> _clientFactory;
    private readonly string _configJson;

    public PluginHostServices(
        ILogger logger,
        IAuthBroker auth,
        IPluginCache cache,
        Func<HttpClient> clientFactory,
        string configJson)
    {
        Logger = logger;
        Auth = auth;
        Cache = cache;
        _clientFactory = clientFactory;
        _configJson = configJson;
    }

    public HttpClient CreateClient() => _clientFactory();

    public IAuthBroker Auth { get; }

    public IPluginCache Cache { get; }

    public ILogger Logger { get; }

    public T GetConfig<T>() where T : class
    {
        var result = JsonSerializer.Deserialize<T>(
            _configJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return result ?? throw new InvalidOperationException(
            $"plugin config could not be bound to {typeof(T).Name}.");
    }
}

/// <summary>
/// A no-op auth broker for the <see cref="AuthScheme.None"/> plugins loaded in Phase 0. It never returns
/// secret material — exactly the contract real schemes must also honor (SDK-CONTRACT.md §3).
/// </summary>
public sealed class NoopAuthBroker : IAuthBroker
{
    public Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct) =>
        Task.FromResult(AuthHandle.Empty);

    public Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// Fallback <see cref="IAuthBrokerFactory"/> used when the host is constructed without the real factory (e.g.
/// the Phase 0 load-only tests). It serves <see cref="AuthScheme.None"/> directly and refuses credentialed
/// schemes — those require the DI-wired <see cref="Calendar.Infrastructure.Auth.AuthBrokerFactory"/> with a vault.
/// </summary>
public sealed class NoopAuthBrokerFactory : IAuthBrokerFactory
{
    public IAuthBroker Create(CredentialContext context)
    {
        if (context.Spec.Scheme == AuthScheme.None)
            return new NoopAuthBroker();
        throw new InvalidOperationException(
            $"Auth scheme '{context.Spec.Scheme}' requires the real AuthBrokerFactory (vault-backed); " +
            "register it via AddPluginAuth().");
    }
}

/// <summary>A process-local, plugin-namespaced cache. Backed by SQLite in a later phase (PLUGIN-HOST.md §7.2).</summary>
public sealed class InMemoryPluginCache : IPluginCache
{
    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);

    public Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        if (_store.TryGetValue(key, out var entry) && !entry.IsExpired)
            return Task.FromResult<byte[]?>(entry.Value);
        _store.TryRemove(key, out _);
        return Task.FromResult<byte[]?>(null);
    }

    public Task SetAsync(string key, byte[] value, TimeSpan? ttl, CancellationToken ct)
    {
        var expiresAt = ttl is { } t ? DateTimeOffset.UtcNow.Add(t) : (DateTimeOffset?)null;
        _store[key] = new Entry(value, expiresAt);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private readonly record struct Entry(byte[] Value, DateTimeOffset? ExpiresAt)
    {
        public bool IsExpired => ExpiresAt is { } e && e <= DateTimeOffset.UtcNow;
    }
}
