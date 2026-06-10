using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Calendar.Application.Aggregation;
using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlaceEntity = Calendar.Domain.Entities.Place;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The host-side routing pipeline (ARCHITECTURE.md §7, geo-routing-plugin.md §4–§5). For a placed event it
/// finds the immediately-preceding placed event at a <em>different</em> <see cref="Place"/>, asks the
/// <see cref="IRouteAggregator"/> for the commute, then HOST-COMPUTES the two fields a provider can't see:
/// <c>LeaveByUtc = nextStart − DurationSec − buffer</c> and <c>Feasible = gap ≥ DurationSec</c>
/// (<c>gap = next.Start − prev.End</c>). Each leg is upserted as a <see cref="RouteLeg"/> keyed by
/// <c>(FromEventId, ToEventId, Mode)</c> — both the precompute cache and the graceful-degradation fallback:
/// a still-valid leg is reused with zero outbound calls, an endpoint move re-routes, and when every provider
/// fails the last-known leg is returned flagged stale. With no <c>geo.route</c> provider (and no cached leg)
/// the result is null — never a crash.
/// </summary>
public sealed class RouteService : IRouteService
{
    /// <summary>Coordinate rounding for the input fingerprint — 5 dp ≈ 1.1 m, matching the Place dedupe grain.</summary>
    private const int FingerprintPrecision = 5;

    private readonly CalendarDbContext _db;
    private readonly IRouteAggregator _aggregator;
    private readonly IPluginRegistry _registry;
    private readonly TimeSpan _buffer;
    private readonly ILogger<RouteService> _logger;

    public RouteService(
        CalendarDbContext db, IRouteAggregator aggregator, IPluginRegistry registry,
        ILogger<RouteService> logger)
        : this(db, aggregator, registry, RouteServiceOptions.DefaultBuffer, logger)
    {
    }

    /// <summary>Test/host seam: inject the per-user "leave by" buffer (parking, walk-in, security).</summary>
    public RouteService(
        CalendarDbContext db, IRouteAggregator aggregator, IPluginRegistry registry,
        TimeSpan buffer, ILogger<RouteService> logger)
    {
        _db = db;
        _aggregator = aggregator;
        _registry = registry;
        _buffer = buffer < TimeSpan.Zero ? TimeSpan.Zero : buffer;
        _logger = logger;
    }

    public async Task<CommuteLeg?> GetCommuteAsync(Guid eventId, TravelMode mode, CancellationToken ct)
    {
        // The arriving ("to") event must itself be placed; a different preceding placed event is the origin.
        var to = await _db.Events
            .Include(e => e.Place)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);
        if (to?.PlaceId is null || to.Place is null)
            return null;

        var from = await FindPrecedingPlacedEventAsync(to, ct).ConfigureAwait(false);
        if (from?.Place is null)
            return null; // first placed event of the day, or the previous event shares this place → no leg.

        var fingerprint = Fingerprint(from.Place, to.Place);

        // Cache: a leg whose endpoints (coords) are unchanged is reused without any outbound call. Time-only
        // edits don't change DurationSec (OSRM is time-agnostic), so we always re-derive leave-by/feasible
        // from the CURRENT event times against the stored duration (geo-routing-plugin.md §5).
        var existing = await _db.RouteLegs
            .FirstOrDefaultAsync(r => r.FromEventId == from.Id && r.ToEventId == to.Id && r.Mode == mode, ct)
            .ConfigureAwait(false);
        if (existing is not null && !existing.IsStale && existing.InputHash == fingerprint)
        {
            Recompute(existing, from, to);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToLeg(existing);
        }

        // Miss / invalidated / previously-stale → route. No provider registered ⇒ skip the call (but a stale
        // last-known leg below still degrades gracefully).
        AggregationResult<RouteResult>? routed = null;
        if (_registry.ForCapability(Capability.GeoRoute).Count > 0)
        {
            routed = await _aggregator.RouteAsync(
                new GeoPoint(from.Place.Lat, from.Place.Lng),
                new GeoPoint(to.Place.Lat, to.Place.Lng),
                mode, to.StartUtc, AggregationOptions.Default, ct).ConfigureAwait(false);
        }

        if (routed is { Value: { } result } && !routed.Stale)
        {
            var row = Upsert(existing, from, to, mode, result.DurationSec, result.Geometry,
                routed.WinningSource ?? result.Source, fingerprint, isStale: false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToLeg(row);
        }

        // Graceful degradation: every eligible provider failed/was over budget. Return the last-known leg
        // (the aggregator's stale value, or our stored row) flagged stale — the chip greys, never vanishes.
        if (routed is { Value: { } stale })
        {
            var row = Upsert(existing, from, to, mode, stale.DurationSec, stale.Geometry,
                routed.WinningSource ?? stale.Source, fingerprint, isStale: true);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToLeg(row);
        }

        if (existing is not null)
        {
            // No live result and no aggregator fallback, but we have a prior leg → serve it stale.
            existing.IsStale = true;
            Recompute(existing, from, to);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ToLeg(existing);
        }

        return null; // never routed, nothing cached → no leg (e.g. no provider at all).
    }

    /// <summary>The nearest earlier event (by end) on a visible calendar that has a DIFFERENT resolved place.</summary>
    private async Task<Event?> FindPrecedingPlacedEventAsync(Event to, CancellationToken ct)
    {
        return await _db.Events
            .Include(e => e.Place)
            .Where(e => e.Id != to.Id
                        && e.PlaceId != null
                        && e.PlaceId != to.PlaceId      // same place ⇒ no commute to compute.
                        && e.EndUtc <= to.StartUtc)
            .OrderByDescending(e => e.EndUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Re-derive the host-computed fields from current event times against the stored duration (§4).</summary>
    private void Recompute(RouteLeg leg, Event from, Event to)
    {
        var gap = to.StartUtc - from.EndUtc;
        leg.LeaveByUtc = to.StartUtc - TimeSpan.FromSeconds(leg.DurationSec) - _buffer;
        leg.Feasible = gap >= TimeSpan.FromSeconds(leg.DurationSec);
    }

    private RouteLeg Upsert(
        RouteLeg? existing, Event from, Event to, TravelMode mode, int durationSec, string? geometry,
        string source, string fingerprint, bool isStale)
    {
        var leg = existing ?? new RouteLeg
        {
            Id = Guid.CreateVersion7(),
            FromEventId = from.Id,
            ToEventId = to.Id,
            Mode = mode,
        };

        leg.DurationSec = durationSec;
        leg.Geometry = geometry;
        leg.Source = source;
        leg.InputHash = fingerprint;
        leg.IsStale = isStale;
        leg.ComputedAtUtc = DateTimeOffset.UtcNow;
        Recompute(leg, from, to);

        if (existing is null)
            _db.RouteLegs.Add(leg);
        return leg;
    }

    private static CommuteLeg ToLeg(RouteLeg r) => new(
        r.FromEventId, r.ToEventId, r.Mode, r.DurationSec,
        r.LeaveByUtc, r.Feasible, r.Source, r.Geometry, r.IsStale);

    /// <summary>Rounded coordinates of both endpoints → a hash that flips only when a Place actually moves.</summary>
    private static string Fingerprint(PlaceEntity from, PlaceEntity to)
    {
        var raw = string.Create(CultureInfo.InvariantCulture,
            $"{Round(from.Lat)},{Round(from.Lng)}->{Round(to.Lat)},{Round(to.Lng)}");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Round(double value) =>
        Math.Round(value, FingerprintPrecision).ToString("F" + FingerprintPrecision, CultureInfo.InvariantCulture);
}

/// <summary>Defaults for <see cref="RouteService"/> (geo-routing-plugin.md §4).</summary>
public static class RouteServiceOptions
{
    /// <summary>Per-user "leave by" buffer absorbing parking / find-the-room / security. Conservative default.</summary>
    public static readonly TimeSpan DefaultBuffer = TimeSpan.FromMinutes(5);
}
