using Calendar.Application.Aggregation;
using Calendar.Application.Fares;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Fares;

/// <summary>
/// Default <see cref="IFareWatchService"/> (ARCHITECTURE.md §14, docs/deep-dives/travel-fares-plugin.md §10).
/// Manages fare watches and polls them through the interchangeable <c>*.price</c> aggregators (fan-out → dedupe
/// → cheapest → source-tag → stale last-known). Each poll appends a timestamped <see cref="FareSample"/> and,
/// when the new low drops below the prior <see cref="FareWatch.LastLowPrice"/> by the watch threshold OR crosses
/// the user's <see cref="FareWatch.TargetPrice"/>, fires <b>exactly one</b> notification through every registered
/// <see cref="INotifier"/>. With no provider registered/configured a poll records nothing and never throws.
/// </summary>
public sealed class FareWatchService : IFareWatchService
{
    private const double DefaultDropThreshold = 0.05; // 5%
    private const string DefaultCurrency = "USD";
    private const string DefaultMarket = "US";

    private readonly CalendarDbContext _db;
    private readonly IFlightPricingAggregator _flights;
    private readonly IStayPricingAggregator _stays;
    private readonly IEnumerable<IFareNotifier> _notifiers;
    private readonly DeviceProvider _device;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILogger<FareWatchService> _logger;

    public FareWatchService(
        CalendarDbContext db,
        IFlightPricingAggregator flights,
        IStayPricingAggregator stays,
        IEnumerable<IFareNotifier> notifiers,
        DeviceProvider device,
        ILogger<FareWatchService> logger)
        : this(db, flights, stays, notifiers, device, logger, () => DateTimeOffset.UtcNow) { }

    public FareWatchService(
        CalendarDbContext db,
        IFlightPricingAggregator flights,
        IStayPricingAggregator stays,
        IEnumerable<IFareNotifier> notifiers,
        DeviceProvider device,
        ILogger<FareWatchService> logger,
        Func<DateTimeOffset> now)
    {
        _db = db;
        _flights = flights;
        _stays = stays;
        _notifiers = notifiers;
        _device = device;
        _logger = logger;
        _now = now;
    }

    /// <inheritdoc />
    public async Task<FareWatchDto> CreateAsync(CreateFareWatchRequest request, CancellationToken ct)
    {
        if (request.RangeEnd < request.RangeStart)
            throw new ArgumentException("RangeEnd must be on or after RangeStart.", nameof(request));
        if (request.Kind == FareKind.Flight && (string.IsNullOrWhiteSpace(request.OriginIata) || string.IsNullOrWhiteSpace(request.DestIata)))
            throw new ArgumentException("A flight watch requires originIata and destIata.", nameof(request));
        if (request.Kind == FareKind.Stay && (request.Lat is null || request.Lng is null))
            throw new ArgumentException("A stay watch requires lat and lng.", nameof(request));

        var deviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        var watch = new FareWatch
        {
            Id = Guid.CreateVersion7(),
            Kind = request.Kind,
            OriginPlaceId = request.OriginPlaceId,
            DestPlaceId = request.DestPlaceId,
            RangeStart = request.RangeStart,
            RangeEnd = request.RangeEnd,
            Pax = Math.Max(1, request.Pax),
            Currency = Coalesce(request.Currency, DefaultCurrency),
            TargetPrice = request.TargetPrice,
            DropThreshold = request.DropThreshold,
            IsActive = true,
            OriginIata = request.OriginIata?.Trim().ToUpperInvariant(),
            DestIata = request.DestIata?.Trim().ToUpperInvariant(),
            Lat = request.Lat,
            Lng = request.Lng,
            RadiusKm = request.RadiusKm,
            Rooms = request.Rooms,
            UpdatedAtUtc = _now(),
            DeviceId = deviceId,
            Lamport = 1,
        };
        _db.FareWatches.Add(watch);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDto(watch);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FareWatchDto>> ListAsync(CancellationToken ct)
    {
        var watches = await _db.FareWatches
            .AsNoTracking()
            .OrderByDescending(w => w.UpdatedAtUtc)
            .ThenByDescending(w => w.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return watches.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var watch = await _db.FareWatches.FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false);
        if (watch is null)
            return false;

        // Soft-delete (tombstone) so the sync layer replicates the removal and polling stops (the query filter
        // hides it from ListAsync/PollAsync immediately).
        watch.IsDeleted = true;
        watch.IsActive = false;
        watch.UpdatedAtUtc = _now();
        watch.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
        watch.Lamport++;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FareSampleDto>> GetHistoryAsync(Guid id, CancellationToken ct)
    {
        var samples = await _db.FareSamples
            .AsNoTracking()
            .Where(s => s.FareWatchId == id)
            .OrderByDescending(s => s.SampledAtUtc)
            .ThenByDescending(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return samples
            .Select(s => new FareSampleDto(s.Id, s.SampledAtUtc, s.Price, s.Currency, s.Source, s.IsStale))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<PollSummary> PollAsync(CancellationToken ct)
    {
        var watches = await _db.FareWatches
            .Where(w => w.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (watches.Count == 0)
            return PollSummary.Empty;

        var samples = 0;
        var notifications = 0;
        foreach (var watch in watches)
        {
            ct.ThrowIfCancellationRequested();
            var (sampled, notified) = await PollOneAsync(watch, ct).ConfigureAwait(false);
            if (sampled) samples++;
            if (notified) notifications++;
        }

        return new PollSummary(watches.Count, samples, notifications);
    }

    private async Task<(bool Sampled, bool Notified)> PollOneAsync(FareWatch watch, CancellationToken ct)
    {
        decimal? low;
        string? source;
        bool stale;
        string? detail;
        try
        {
            (low, source, stale, detail) = watch.Kind == FareKind.Flight
                ? await PollFlightAsync(watch, ct).ConfigureAwait(false)
                : await PollStayAsync(watch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Pricing must degrade gracefully — a provider/aggregator fault never breaks the poll loop.
            _logger.LogWarning(ex, "Fare watch {WatchId} poll faulted; skipping this sweep.", watch.Id);
            return (false, false);
        }

        if (low is not { } newLow)
        {
            // No provider/no offers/no history → nothing to record. No overlay, no crash (§13).
            _logger.LogDebug("Fare watch {WatchId} produced no price this poll.", watch.Id);
            return (false, false);
        }

        var sampledAt = _now();
        _db.FareSamples.Add(new FareSample
        {
            Id = Guid.CreateVersion7(),
            FareWatchId = watch.Id,
            SampledAtUtc = sampledAt,
            Price = newLow,
            Currency = watch.Currency,
            Source = source ?? "unknown",
            IsStale = stale,
            Detail = detail,
        });

        var prior = watch.LastLowPrice;
        var fired = DecideNotification(watch, newLow, prior, source);

        // Update the running low for the next poll's drop comparison (track the cheapest seen).
        if (prior is null || newLow < prior)
        {
            watch.LastLowPrice = newLow;
            watch.UpdatedAtUtc = sampledAt;
            watch.DeviceId = await _device.GetDeviceIdAsync(ct).ConfigureAwait(false);
            watch.Lamport++;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var notified = false;
        if (fired is { } notification)
        {
            await FireAsync(notification, ct).ConfigureAwait(false);
            notified = true;
        }

        return (true, notified);
    }

    /// <summary>
    /// Decide whether this new low fires a single notification: a target-cross takes precedence (and is the more
    /// actionable signal), otherwise a drop of at least the watch threshold versus the prior low. A first sample,
    /// a no-change, or a price-rise does not fire (travel-fares-plugin.md §13).
    /// </summary>
    private FareDropNotification? DecideNotification(FareWatch watch, decimal newLow, decimal? prior, string? source)
    {
        var label = Label(watch);

        if (watch.TargetPrice is { } target && newLow <= target &&
            (prior is null || prior > target))
        {
            // Cross under the target — fire once on the crossing edge, not on every subsequent under-target poll.
            return new FareDropNotification(
                watch.Id, NotificationKind.FareTarget,
                $"{label} hit your target: {Money(newLow, watch.Currency)} (target {Money(target, watch.Currency)})",
                newLow, watch.Currency, target, source);
        }

        if (prior is { } priorLow && newLow < priorLow)
        {
            var threshold = watch.DropThreshold ?? DefaultDropThreshold;
            var drop = (double)((priorLow - newLow) / priorLow);
            if (drop >= threshold)
            {
                return new FareDropNotification(
                    watch.Id, NotificationKind.FareDrop,
                    $"{label} fell to {Money(newLow, watch.Currency)} (was {Money(priorLow, watch.Currency)})",
                    newLow, watch.Currency, priorLow, source);
            }
        }

        return null;
    }

    private async Task FireAsync(FareDropNotification notification, CancellationToken ct)
    {
        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.NotifyAsync(notification, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Notifier {Channel} failed to deliver for watch {WatchId}.",
                    notifier.Channel, notification.FareWatchId);
            }
        }
    }

    private async Task<(decimal? Low, string? Source, bool Stale, string? Detail)> PollFlightAsync(FareWatch watch, CancellationToken ct)
    {
        var query = new FareQuery(
            From: watch.OriginIata ?? string.Empty,
            To: watch.DestIata ?? string.Empty,
            DepartDate: watch.RangeStart,
            ReturnDate: watch.RangeEnd > watch.RangeStart ? watch.RangeEnd : null,
            Adults: Math.Max(1, watch.Pax),
            Cabin: CabinClass.Economy,
            Currency: watch.Currency,
            Market: DefaultMarket);

        var result = await _flights.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
        if (!result.HasValue || result.Value is not { Count: > 0 } offers)
            return (null, null, false, null);

        var cheapest = offers.MinBy(o => o.Price)!;
        var detail = $"{{\"carrier\":\"{cheapest.Carrier}\",\"flightNo\":\"{cheapest.FlightNo}\",\"deepLink\":\"{cheapest.DeepLink}\"}}";
        return (cheapest.Price, result.WinningSource ?? cheapest.Source, result.Stale || cheapest.Stale, detail);
    }

    private async Task<(decimal? Low, string? Source, bool Stale, string? Detail)> PollStayAsync(FareWatch watch, CancellationToken ct)
    {
        var query = new StayQuery(
            Lat: watch.Lat ?? 0,
            Lng: watch.Lng ?? 0,
            RadiusKm: Math.Max(1, watch.RadiusKm ?? 5),
            CheckIn: watch.RangeStart,
            CheckOut: watch.RangeEnd,
            Adults: Math.Max(1, watch.Pax),
            Rooms: Math.Max(1, watch.Rooms ?? 1),
            Currency: watch.Currency,
            Market: DefaultMarket);

        var result = await _stays.SearchAsync(query, AggregationOptions.FanOut, ct).ConfigureAwait(false);
        if (!result.HasValue || result.Value is not { Count: > 0 } offers)
            return (null, null, false, null);

        var cheapest = offers.MinBy(o => o.PriceTotal)!;
        var detail = $"{{\"placeLabel\":\"{cheapest.PlaceLabel}\",\"deepLink\":\"{cheapest.DeepLink}\"}}";
        return (cheapest.PriceTotal, result.WinningSource ?? cheapest.Source, result.Stale || cheapest.Stale, detail);
    }

    private static string Label(FareWatch watch) => watch.Kind == FareKind.Flight
        ? $"{watch.OriginIata}→{watch.DestIata}"
        : $"Stay @ {watch.Lat:0.###},{watch.Lng:0.###}";

    private static string Money(decimal amount, string currency) =>
        $"{amount:0.##} {currency}";

    private static FareWatchDto ToDto(FareWatch w) => new(
        w.Id, w.Kind, w.RangeStart, w.RangeEnd, w.Pax, w.Currency,
        w.TargetPrice, w.LastLowPrice, w.DropThreshold ?? DefaultDropThreshold, w.IsActive,
        w.OriginIata, w.DestIata, w.Lat, w.Lng, w.RadiusKm, w.Rooms);

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
