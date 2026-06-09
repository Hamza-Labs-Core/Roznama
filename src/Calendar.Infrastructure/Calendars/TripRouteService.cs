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
/// Stitches placed Travel-category events into trip routes for the Map view (ROADMAP Phase 5; UI-WIREFRAMES
/// §6). Events are ordered by start, split into trips at a >7-day gap, and each consecutive pair of distinct
/// places becomes a leg. Leg geometry goes through the <c>geo.route</c> aggregator and is cached in the same
/// <c>RouteLeg</c> table the commute pipeline uses (keyed by event pair + mode, fingerprinted by endpoint
/// coordinates) — so repeated map opens cost zero outbound calls. No provider (or a failed route, e.g. a
/// flight over water) degrades to a null geometry and the client draws a straight line; never a crash.
/// </summary>
public sealed class TripRouteService : ITripRouteService
{
    private const string TravelCategory = "Travel";
    private static readonly TimeSpan TripGap = TimeSpan.FromDays(7);
    private const int FingerprintPrecision = 5;

    private readonly CalendarDbContext _db;
    private readonly IRouteAggregator _aggregator;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<TripRouteService> _logger;

    public TripRouteService(
        CalendarDbContext db, IRouteAggregator aggregator, IPluginRegistry registry,
        ILogger<TripRouteService> logger)
    {
        _db = db;
        _aggregator = aggregator;
        _registry = registry;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TripRoute>> GetTripRoutesAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var stops = await (
            from e in _db.Events
            join c in _db.Calendars on e.CalendarId equals c.Id
            where c.IsVisible
                  && e.PlaceId != null
                  && e.StartUtc < toUtc && e.EndUtc >= fromUtc
                  && e.Categories.Any(cat => cat.Name == TravelCategory)
            orderby e.StartUtc
            select e)
            .Include(e => e.Place)
            .ToListAsync(ct).ConfigureAwait(false);

        var trips = new List<TripRoute>();
        var hasRouteProvider = _registry.ForCapability(Capability.GeoRoute).Count > 0;

        foreach (var trip in GroupIntoTrips(stops))
        {
            var legs = new List<TripLeg>();
            for (var i = 1; i < trip.Count; i++)
            {
                var from = trip[i - 1];
                var to = trip[i];
                if (from.PlaceId == to.PlaceId)
                    continue;   // same place (e.g. hotel night markers) → nothing to draw.

                var (geometry, source) = await ResolveGeometryAsync(from, to, hasRouteProvider, ct).ConfigureAwait(false);
                legs.Add(new TripLeg(
                    from.Id, to.Id,
                    from.Place!.Label, to.Place!.Label,
                    from.Place.Lat, from.Place.Lng, to.Place.Lat, to.Place.Lng,
                    from.EndUtc, to.StartUtc,
                    geometry, source));
            }

            if (legs.Count > 0)
            {
                var name = $"{legs[0].FromLabel} → {legs[^1].ToLabel}";
                trips.Add(new TripRoute(trip[0].Id, name, legs));
            }
        }

        return trips;
    }

    /// <summary>Consecutive travel events belong to one trip until a gap longer than <see cref="TripGap"/>.</summary>
    private static IEnumerable<List<Event>> GroupIntoTrips(List<Event> stops)
    {
        var current = new List<Event>();
        foreach (var stop in stops)
        {
            if (current.Count > 0 && stop.StartUtc - current[^1].EndUtc > TripGap)
            {
                yield return current;
                current = new List<Event>();
            }
            current.Add(stop);
        }
        if (current.Count > 0)
            yield return current;
    }

    /// <summary>
    /// Leg geometry via the shared <c>RouteLeg</c> cache, then the aggregator. Null geometry (no provider,
    /// failure, unroutable water crossing) means "straight line" to the client — legs always render.
    /// </summary>
    private async Task<(string? Geometry, string? Source)> ResolveGeometryAsync(
        Event from, Event to, bool hasRouteProvider, CancellationToken ct)
    {
        var fingerprint = Fingerprint(from.Place!, to.Place!);
        var cached = await _db.RouteLegs
            .FirstOrDefaultAsync(r => r.FromEventId == from.Id && r.ToEventId == to.Id && r.Mode == TravelMode.Drive, ct)
            .ConfigureAwait(false);
        if (cached is not null && cached.InputHash == fingerprint)
            return (cached.Geometry, cached.Source);

        if (!hasRouteProvider)
            return (null, null);

        try
        {
            var routed = await _aggregator.RouteAsync(
                new GeoPoint(from.Place!.Lat, from.Place.Lng),
                new GeoPoint(to.Place!.Lat, to.Place.Lng),
                TravelMode.Drive, to.StartUtc, AggregationOptions.Default, ct).ConfigureAwait(false);
            if (routed is not { Value: { } result })
                return (null, null);

            var leg = cached ?? new RouteLeg
            {
                Id = Guid.CreateVersion7(),
                FromEventId = from.Id,
                ToEventId = to.Id,
                Mode = TravelMode.Drive,
            };
            leg.DurationSec = result.DurationSec;
            leg.Geometry = result.Geometry;
            leg.Source = routed.WinningSource ?? result.Source;
            leg.InputHash = fingerprint;
            leg.IsStale = routed.Stale;
            leg.ComputedAtUtc = DateTimeOffset.UtcNow;
            leg.LeaveByUtc = to.StartUtc - TimeSpan.FromSeconds(result.DurationSec);
            leg.Feasible = true;    // trips are planned travel, not back-to-back conflicts.
            if (cached is null)
                _db.RouteLegs.Add(leg);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return (leg.Geometry, leg.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Trip leg routing failed for {From} → {To}; drawing a straight line.", from.Id, to.Id);
            return (null, null);
        }
    }

    private static string Fingerprint(PlaceEntity from, PlaceEntity to)
    {
        var raw = string.Create(CultureInfo.InvariantCulture,
            $"{Round(from.Lat)},{Round(from.Lng)}->{Round(to.Lat)},{Round(to.Lng)}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Round(double value) =>
        Math.Round(value, FingerprintPrecision).ToString("F" + FingerprintPrecision, CultureInfo.InvariantCulture);
}
