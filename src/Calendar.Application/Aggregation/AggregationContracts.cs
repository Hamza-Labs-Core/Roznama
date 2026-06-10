using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Aggregation;

/// <summary>
/// How the aggregator dispatches an interchangeable-capability query across the eligible providers
/// (ARCHITECTURE.md §15, PLUGIN-HOST.md §9).
/// </summary>
public enum AggregationMode
{
    /// <summary>
    /// Cheap default: try eligible providers in policy order and return the first healthy, non-empty
    /// result. A throwing or empty provider is skipped; the next is tried.
    /// </summary>
    Failover,

    /// <summary>
    /// Best coverage: call all eligible providers, normalize, dedupe identical results, pick the best
    /// (cheapest fare / fastest route / first place), and tag the winning <c>Source</c>.
    /// </summary>
    FanOutMerge
}

/// <summary>
/// Per-query knobs for an aggregator call (ARCHITECTURE.md §15). Defaults to cheap <see cref="AggregationMode.Failover"/>.
/// </summary>
public sealed record AggregationOptions(
    AggregationMode Mode = AggregationMode.Failover,
    TimeSpan? CacheTtl = null)
{
    /// <summary>The cheap default policy: failover, with the aggregator's standard cache TTL.</summary>
    public static readonly AggregationOptions Default = new();

    /// <summary>Best-coverage fan-out + merge.</summary>
    public static readonly AggregationOptions FanOut = new(AggregationMode.FanOutMerge);
}

/// <summary>
/// The outcome of an aggregator query, carrying both the value and the observability the host records per
/// query (which plugins were tried, hit/miss, the winning source, and whether the value is a stale
/// last-known fallback) — ARCHITECTURE.md §15 "Observability" / "Graceful degradation".
/// </summary>
/// <typeparam name="T">The capability result type (e.g. <see cref="RouteResult"/>, <see cref="Place"/>).</typeparam>
public sealed record AggregationResult<T>(
    T? Value,
    string? WinningSource,
    bool Stale,
    bool FromCache,
    IReadOnlyList<ProviderAttempt> Attempts)
{
    /// <summary>True when a value (live, cached, or stale last-known) was produced.</summary>
    public bool HasValue => Value is not null;

    /// <summary>A miss: every eligible provider failed/was empty and no last-known result existed.</summary>
    public static AggregationResult<T> Empty(IReadOnlyList<ProviderAttempt> attempts) =>
        new(default, null, Stale: false, FromCache: false, attempts);
}

/// <summary>One provider's participation in a query — for the per-query observability trail.</summary>
public sealed record ProviderAttempt(
    string PluginId,
    ProviderOutcome Outcome,
    TimeSpan Latency,
    string? Detail = null);

/// <summary>What happened when the aggregator considered/called one provider.</summary>
public enum ProviderOutcome
{
    /// <summary>Skipped before any call because its coverage can't serve the query (ARCHITECTURE.md §15).</summary>
    SkippedCoverage,

    /// <summary>Called and returned a usable, non-empty result.</summary>
    Success,

    /// <summary>Called but returned nothing (empty list / null).</summary>
    Empty,

    /// <summary>Threw or otherwise faulted — the aggregator failed over to the next provider.</summary>
    Faulted
}
