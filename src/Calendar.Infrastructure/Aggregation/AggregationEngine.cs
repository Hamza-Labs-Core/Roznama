using System.Diagnostics;
using Calendar.Application.Aggregation;
using Calendar.Application.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// The shared dispatch core every interchangeable-capability aggregator runs through (ARCHITECTURE.md §15,
/// PLUGIN-HOST.md §9). It resolves eligible providers from the registry, applies a per-provider coverage
/// filter, then either fails over (first healthy good result) or fans out + merges. On a fresh cache hit it
/// short-circuits; when every provider fails/empties it degrades to the last-known stored value with
/// <c>Stale = true</c>. It is deliberately provider-agnostic — it never names "OSRM" or "Duffel".
/// </summary>
public sealed class AggregationEngine
{
    /// <summary>Default freshness TTL for the small result cache when a call doesn't override it.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IPluginRegistry _registry;
    private readonly IAggregationResultCache _cache;
    private readonly ILogger _logger;

    public AggregationEngine(IPluginRegistry registry, IAggregationResultCache cache, ILogger logger)
    {
        _registry = registry;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Run one aggregated query.
    /// </summary>
    /// <typeparam name="TPlugin">The capability interface (e.g. <see cref="IRouteProvider"/>).</typeparam>
    /// <typeparam name="TResult">The cacheable, returnable result type (a single DTO or a list).</typeparam>
    /// <param name="capability">Which capability to resolve from the registry.</param>
    /// <param name="cacheKey">Fully-qualified per-capability+query cache key.</param>
    /// <param name="options">Mode + TTL (null → cheap failover with the default TTL).</param>
    /// <param name="isEligible">Coverage filter: false skips the provider before any call (no fan-out cost).</param>
    /// <param name="invoke">Calls the provider; returns a non-empty result or null/empty when it has nothing.</param>
    /// <param name="isNonEmpty">True if a returned result is usable (non-null, non-empty list).</param>
    /// <param name="merge">
    /// Fan-out reducer: dedupe + pick best across all successful results. Returns the merged value tagged
    /// with the winning source — a single provider id for a single-DTO capability, or a "+"-joined set for a
    /// list capability whose items each already carry their own Source.
    /// </param>
    /// <param name="stamp">Stamps the winning provider id onto the chosen result (Source/attribution).</param>
    /// <param name="ct">Cancels the dispatch; an eligible provider's cancellation propagates.</param>
    public async Task<AggregationResult<TResult>> RunAsync<TPlugin, TResult>(
        Capability capability,
        string cacheKey,
        AggregationOptions? options,
        Func<TPlugin, bool> isEligible,
        Func<TPlugin, CancellationToken, Task<TResult?>> invoke,
        Func<TResult?, bool> isNonEmpty,
        Func<IReadOnlyList<SourcedResult<TResult>>, SourcedResult<TResult>> merge,
        Func<TResult, string, TResult> stamp,
        CancellationToken ct)
        where TPlugin : class, IPlugin
        where TResult : class
    {
        options ??= AggregationOptions.Default;
        var ttl = options.CacheTtl ?? DefaultCacheTtl;

        // 1. Fresh cache hit short-circuits the providers entirely.
        if (_cache.TryGetFresh<TResult>(cacheKey, out var cached))
        {
            _logger.LogDebug("Aggregator cache hit for {Capability} ({Key}).", capability, cacheKey);
            return new AggregationResult<TResult>(cached, WinningSource: null, Stale: false, FromCache: true,
                Array.Empty<ProviderAttempt>());
        }

        // 2. Resolve + coverage-filter the eligible providers.
        var registrations = _registry.ForCapability(capability);
        var attempts = new List<ProviderAttempt>(registrations.Count);
        var eligible = new List<(string Id, TPlugin Plugin)>(registrations.Count);

        foreach (var reg in registrations)
        {
            if (reg.Instance.Plugin is not TPlugin plugin)
                continue;
            if (!isEligible(plugin))
            {
                attempts.Add(new ProviderAttempt(reg.Id, ProviderOutcome.SkippedCoverage, TimeSpan.Zero,
                    "coverage does not serve the query"));
                continue;
            }
            eligible.Add((reg.Id, plugin));
        }

        // 3. Dispatch by mode.
        var successes = new List<SourcedResult<TResult>>();
        foreach (var (id, plugin) in eligible)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await invoke(plugin, ct).ConfigureAwait(false);
                sw.Stop();
                if (isNonEmpty(result))
                {
                    successes.Add(new SourcedResult<TResult>(id, result!));
                    attempts.Add(new ProviderAttempt(id, ProviderOutcome.Success, sw.Elapsed));
                    if (options.Mode == AggregationMode.Failover)
                        break; // cheap default: first healthy good result wins.
                }
                else
                {
                    attempts.Add(new ProviderAttempt(id, ProviderOutcome.Empty, sw.Elapsed));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                attempts.Add(new ProviderAttempt(id, ProviderOutcome.Faulted, sw.Elapsed, ex.Message));
                _logger.LogWarning(ex, "Provider {PluginId} faulted serving {Capability}; failing over.", id, capability);
                // skip — try the next provider.
            }
        }

        // 4. Produce the winner.
        if (successes.Count > 0)
        {
            var winner = options.Mode == AggregationMode.FanOutMerge ? merge(successes) : successes[0];
            var chosen = stamp(winner.Value, winner.Source);
            _cache.Set(cacheKey, chosen, ttl);
            return new AggregationResult<TResult>(chosen, winner.Source, Stale: false, FromCache: false, attempts);
        }

        // 5. Graceful degradation: serve last-known stored result, flagged stale.
        if (_cache.TryGetLastKnown<TResult>(cacheKey, out var lastKnown, out _))
        {
            _logger.LogInformation("All {Capability} providers down/empty; serving stale last-known ({Key}).",
                capability, cacheKey);
            return new AggregationResult<TResult>(lastKnown, WinningSource: null, Stale: true, FromCache: true, attempts);
        }

        return AggregationResult<TResult>.Empty(attempts);
    }

    /// <summary>Fan-out attribution for a list capability whose items each carry their own Source: the "+"-joined set of providers actually merged.</summary>
    public static string JoinSources<TResult>(IReadOnlyList<SourcedResult<TResult>> successes)
        => string.Join("+", successes.Select(s => s.Source));
}

/// <summary>A single provider's successful result, tagged with the plugin id that produced it.</summary>
public readonly record struct SourcedResult<TResult>(string Source, TResult Value);
