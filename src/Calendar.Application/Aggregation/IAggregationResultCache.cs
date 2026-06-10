namespace Calendar.Application.Aggregation;

/// <summary>
/// A small in-memory result cache with TTL fronting every interchangeable-capability query
/// (ARCHITECTURE.md §15 "Fresh cache?"). It also retains the last-known good value per key — even past
/// its freshness TTL — so the aggregator can degrade gracefully to a stale result when every provider is
/// down (ARCHITECTURE.md §15 "Graceful degradation").
/// </summary>
public interface IAggregationResultCache
{
    /// <summary>A fresh hit for <paramref name="key"/> (within TTL), or null on miss/expiry.</summary>
    bool TryGetFresh<T>(string key, out T value) where T : class;

    /// <summary>
    /// The last-known good value for <paramref name="key"/> regardless of freshness, with the instant it
    /// was originally retrieved — used for the stale fallback. Null when nothing was ever stored.
    /// </summary>
    bool TryGetLastKnown<T>(string key, out T value, out DateTimeOffset retrievedAt) where T : class;

    /// <summary>Store a fresh good value with a freshness <paramref name="ttl"/>; it also becomes the new last-known.</summary>
    void Set<T>(string key, T value, TimeSpan ttl) where T : class;
}
