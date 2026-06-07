using Calendar.Plugin.Abstractions;

namespace Calendar.Application.Calendars;

/// <summary>
/// The host-side routing pipeline (ARCHITECTURE.md §7, geo-routing-plugin.md §4–§5). For a placed event it
/// computes the commute from the immediately-preceding placed event through the <c>geo.route</c> aggregator,
/// then HOST-COMPUTES the two fields a provider can't see: <c>LeaveByUtc = nextStart − DurationSec − buffer</c>
/// and <c>Feasible = gap ≥ DurationSec</c> (<c>gap = next.Start − prev.End</c>). The leg is persisted as a
/// <c>RouteLeg</c> keyed by <c>(FromEventId, ToEventId, Mode)</c> so it is both a precompute cache and the
/// graceful-degradation fallback: when every provider fails the last-known leg is returned flagged stale.
/// Degrades gracefully: no <c>Place</c> on an endpoint, no preceding event, or no <c>geo.route</c> provider
/// (and no cached leg) → null, never a throw.
/// </summary>
public interface IRouteService
{
    /// <summary>
    /// Resolve the commute leg arriving at the given event in the given travel mode. Reads a still-valid
    /// cached <c>RouteLeg</c> without touching a provider; on a miss (or invalidated leg) it routes, recomputes
    /// leave-by + feasibility, and upserts the leg. Returns null when no leg applies (see the interface remarks).
    /// </summary>
    Task<CommuteLeg?> GetCommuteAsync(Guid eventId, TravelMode mode, CancellationToken ct);
}

/// <summary>
/// A computed commute between two consecutive placed events — the read model behind the commute chip,
/// "leave by HH:MM" hint, and "you can't make it" conflict warning (geo-routing-plugin.md §4).
/// </summary>
public sealed record CommuteLeg(
    Guid FromEventId,
    Guid ToEventId,
    TravelMode Mode,
    int DurationSec,
    DateTimeOffset? LeaveByUtc,                  // host-computed: nextStart − DurationSec − buffer
    bool Feasible,                              // host-computed: gap ≥ DurationSec ("you can't make it" when false)
    string Source,                              // winning provider id (or the stale leg's recorded source)
    string? Geometry,
    bool IsStale);                              // true ⇒ served as last-known after every provider failed
