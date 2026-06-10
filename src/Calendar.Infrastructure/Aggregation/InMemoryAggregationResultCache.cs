using System.Collections.Concurrent;
using Calendar.Application.Aggregation;

namespace Calendar.Infrastructure.Aggregation;

/// <summary>
/// A process-local result cache with a freshness TTL plus an indefinite last-known slot per key
/// (ARCHITECTURE.md §15). A fresh hit short-circuits the providers; an expired-but-present entry is the
/// stale fallback the aggregator serves when every provider is down. Keyed per capability+query by the
/// aggregator, so geocode/route/fare results never collide.
/// </summary>
public sealed class InMemoryAggregationResultCache : IAggregationResultCache
{
    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public InMemoryAggregationResultCache() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Test seam: inject a clock so TTL expiry is deterministic.</summary>
    public InMemoryAggregationResultCache(Func<DateTimeOffset> now) => _now = now;

    public bool TryGetFresh<T>(string key, out T value) where T : class
    {
        if (_store.TryGetValue(key, out var entry) && entry.FreshUntil > _now() && entry.Value is T typed)
        {
            value = typed;
            return true;
        }
        value = null!;
        return false;
    }

    public bool TryGetLastKnown<T>(string key, out T value, out DateTimeOffset retrievedAt) where T : class
    {
        if (_store.TryGetValue(key, out var entry) && entry.Value is T typed)
        {
            value = typed;
            retrievedAt = entry.RetrievedAt;
            return true;
        }
        value = null!;
        retrievedAt = default;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl) where T : class
    {
        var now = _now();
        _store[key] = new Entry(value, now, now.Add(ttl));
    }

    private sealed record Entry(object Value, DateTimeOffset RetrievedAt, DateTimeOffset FreshUntil);
}
