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
using SdkPlace = Calendar.Plugin.Abstractions.Place;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The host-side geocoding pipeline (ARCHITECTURE.md §7, geo-geocoding-places-plugin.md §3). Resolves an
/// event's free-text <c>Location</c> to a <see cref="Place"/> through the <see cref="IGeocodeAggregator"/>,
/// caching results in <see cref="GeocodeCache"/> under a SHA-256 of the normalized query so an identical
/// query never makes a second outbound call. Resolved coordinates promote to a <see cref="Place"/> row that
/// is deduped by <see cref="Place.NormalizedKey"/> (rounded lat/lng + lower-cased label), so two events at
/// the same address share one pin / one route leg. Degrades gracefully: with no registered
/// <c>geo.geocode</c> provider it is a quiet no-op — events keep their raw text and simply get no pin.
/// </summary>
public sealed class GeocodeService : IGeocodeService
{
    /// <summary>Coordinate rounding for the Place dedupe key — 5 dp ≈ 1.1 m, fine enough to collapse one address.</summary>
    private const int DedupePrecision = 5;

    private readonly CalendarDbContext _db;
    private readonly IGeocodeAggregator _aggregator;
    private readonly IPluginRegistry _registry;
    private readonly DeviceProvider _device;
    private readonly ILogger<GeocodeService> _logger;

    public GeocodeService(
        CalendarDbContext db, IGeocodeAggregator aggregator, IPluginRegistry registry,
        DeviceProvider device, ILogger<GeocodeService> logger)
    {
        _db = db;
        _aggregator = aggregator;
        _registry = registry;
        _device = device;
        _logger = logger;
    }

    public async Task<int> GeocodePendingEventsAsync(Guid calendarId, CancellationToken ct)
    {
        // Graceful degradation: no geo.geocode provider registered → do nothing, never crash (§7).
        if (_registry.ForCapability(Capability.GeoGeocode).Count == 0)
            return 0;

        var pending = await _db.Events
            .Where(e => e.CalendarId == calendarId && e.PlaceId == null &&
                        e.Location != null && e.Location != "")
            .ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0)
            return 0;

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var resolved = 0;

        foreach (var ev in pending)
        {
            ct.ThrowIfCancellationRequested();
            var place = await ResolveAsync(ev.Location!, ct).ConfigureAwait(false);
            if (place is null)
                continue; // no hit — keep the raw text, retry on a later sync (no empty Place row).

            var row = await GetOrCreatePlaceAsync(place, deviceId, ct).ConfigureAwait(false);
            ev.PlaceId = row.Id;
            resolved++;
        }

        if (resolved > 0)
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>Cache-first resolution: a hit returns the stored coordinate with zero outbound calls (§3).</summary>
    private async Task<SdkPlace?> ResolveAsync(string location, CancellationToken ct)
    {
        var normalized = NormalizeQuery(location);
        if (normalized.Length == 0)
            return null;

        var hash = Sha256Hex(normalized);

        // Cache lookup checks both persisted rows and ones added earlier in THIS run (the change tracker's
        // Local view), so two events sharing a query in one pass make exactly one outbound call + one row.
        var cached = _db.GeocodeCache.Local.FirstOrDefault(g => g.QueryHash == hash && !g.IsReverse)
            ?? await _db.GeocodeCache.FirstOrDefaultAsync(g => g.QueryHash == hash && !g.IsReverse, ct)
                .ConfigureAwait(false);
        if (cached is not null)
            return new SdkPlace(cached.Lat, cached.Lng, location, location, cached.Source);

        var result = await _aggregator.GeocodeAsync(location, bias: null, AggregationOptions.Default, ct)
            .ConfigureAwait(false);
        if (result.Value is not { } hit)
            return null;

        _db.GeocodeCache.Add(new GeocodeCache
        {
            Id = Guid.CreateVersion7(),
            Query = normalized,
            QueryHash = hash,
            Lat = hit.Lat,
            Lng = hit.Lng,
            Source = hit.Source,
            IsReverse = false,
            ResolvedAtUtc = DateTimeOffset.UtcNow,
        });

        return hit;
    }

    /// <summary>Promote a resolved coordinate to a deduped <see cref="Place"/> (§3 step 4).</summary>
    private async Task<PlaceEntity> GetOrCreatePlaceAsync(SdkPlace place, Guid deviceId, CancellationToken ct)
    {
        var key = NormalizedKey(place.Lat, place.Lng, place.Label);

        // Dedupe against both persisted Places and ones added earlier in THIS run, so same-address events in
        // one pass collapse to a single pin (NormalizedKey is unique — a duplicate insert would else throw).
        var existing = _db.Places.Local.FirstOrDefault(p => p.NormalizedKey == key)
            ?? await _db.Places.FirstOrDefaultAsync(p => p.NormalizedKey == key, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;

        var row = new PlaceEntity
        {
            Id = Guid.CreateVersion7(),
            Label = place.Label,
            Lat = place.Lat,
            Lng = place.Lng,
            Address = place.Address,
            Source = place.Source,
            NormalizedKey = key,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            Lamport = 1,
        };
        _db.Places.Add(row);
        return row;
    }

    /// <summary>Lower-case, whitespace-collapsed query (§3 step 1) — the cache/dedupe normal form.</summary>
    internal static string NormalizeQuery(string query) =>
        string.Join(' ', query.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Proximity+label dedupe key: rounded lat/lng + lower-cased label (DATA-SCHEMA §2.5).</summary>
    internal static string NormalizedKey(double lat, double lng, string label)
    {
        var roundedLat = Math.Round(lat, DedupePrecision).ToString("F" + DedupePrecision, CultureInfo.InvariantCulture);
        var roundedLng = Math.Round(lng, DedupePrecision).ToString("F" + DedupePrecision, CultureInfo.InvariantCulture);
        return $"{roundedLat},{roundedLng}|{label.Trim().ToLowerInvariant()}";
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }
}
