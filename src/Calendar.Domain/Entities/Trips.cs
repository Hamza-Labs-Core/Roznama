using Calendar.Plugin.Abstractions;

namespace Calendar.Domain.Entities;

/// <summary>A trip grouping (DATA-SCHEMA §2.6; ARCHITECTURE §14). User planning state — replicates.</summary>
public sealed class Trip : ISyncEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>A flight/stay/car/rail/activity within a trip (DATA-SCHEMA §2.6).</summary>
public sealed class TripItem : ISyncEntity
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public TripItemKind Kind { get; set; }
    public TripItemStatus Status { get; set; }
    public Guid? PlaceId { get; set; }
    public string? Confirmation { get; set; }
    public DateTimeOffset? StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }

    /// <summary>The projected Travel event for a booked item (→ <see cref="Event.Id"/>).</summary>
    public Guid? ProjectedEventId { get; set; }

    /// <summary>Provider-specific bag (carrier/flightNo, nightly rate, deep-link, source) as a JSON column.</summary>
    public string? Details { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>A tracked route/stay + date range + pax (DATA-SCHEMA §2.6; ARCHITECTURE §14).</summary>
public sealed class FareWatch : ISyncEntity
{
    public Guid Id { get; set; }
    public FareKind Kind { get; set; }
    public Guid? OriginPlaceId { get; set; }
    public Guid? DestPlaceId { get; set; }
    public DateOnly RangeStart { get; set; }
    public DateOnly RangeEnd { get; set; }
    public int Pax { get; set; }
    public string Currency { get; set; } = default!;
    public decimal? TargetPrice { get; set; }
    public decimal? LastLowPrice { get; set; }
    public bool IsActive { get; set; } = true;

    // ── Query payload (ARCHITECTURE §14, travel-fares-plugin.md §10) ──
    // A watch must reconstruct the FareQuery/StayQuery the poll runs through the aggregator. Flights key on
    // IATA codes; stays key on a coordinate + radius. These are additive nullable columns over the §2.6 base.

    /// <summary>Flight: origin IATA code (e.g. "JFK"); null for stays.</summary>
    public string? OriginIata { get; set; }

    /// <summary>Flight: destination IATA code (e.g. "LHR"); null for stays.</summary>
    public string? DestIata { get; set; }

    /// <summary>Stay: search-centre latitude; null for flights.</summary>
    public double? Lat { get; set; }

    /// <summary>Stay: search-centre longitude; null for flights.</summary>
    public double? Lng { get; set; }

    /// <summary>Stay: search radius in km (defaults applied at poll time).</summary>
    public int? RadiusKm { get; set; }

    /// <summary>Stay: number of rooms.</summary>
    public int? Rooms { get; set; }

    /// <summary>
    /// Relative drop threshold (0..1) that, when the new low falls below the prior <see cref="LastLowPrice"/>
    /// by at least this fraction, fires a fare-drop notification. Defaults to 0.05 (5%) when null.
    /// </summary>
    public double? DropThreshold { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// A persisted in-app notification (ARCHITECTURE §14, travel-fares-plugin.md §10). The fare-drop/target event
/// is recorded here so the UI and <c>GET /api/notifications</c> can read it newest-first. Email/webhook delivery
/// is a separate stubbed channel; this log is the source of truth the point of the feature hinges on.
/// </summary>
public sealed class NotificationLog
{
    public Guid Id { get; set; }
    public Guid? FareWatchId { get; set; }
    public NotificationKind Kind { get; set; }
    public NotificationChannel Channel { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Human-readable summary, e.g. "JFK→LHR fell to $412 (was $480) on duffel".</summary>
    public string Message { get; set; } = default!;

    /// <summary>The new low that triggered the notification.</summary>
    public decimal Price { get; set; }
    public string Currency { get; set; } = default!;

    /// <summary>The prior low (drop) or target (target-cross) the new low beat; for the chart/tooltip.</summary>
    public decimal? PreviousPrice { get; set; }

    /// <summary>Winning provider source tag ("duffel" | "kiwi" | ...).</summary>
    public string? Source { get; set; }

    /// <summary>True once a non-in-app channel acknowledged delivery (stubs flip this immediately).</summary>
    public bool Delivered { get; set; }
}

/// <summary>A price-history series point (DATA-SCHEMA §2.6). Provider-derived — no sync-metadata.</summary>
public sealed class FareSample
{
    public Guid Id { get; set; }
    public Guid FareWatchId { get; set; }
    public DateTimeOffset SampledAtUtc { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = default!;
    public string Source { get; set; } = default!;

    /// <summary>True when the point is a degraded last-known.</summary>
    public bool IsStale { get; set; }

    /// <summary>Carrier/flightNo/deepLink snapshot for the overlay/chart (JSON column).</summary>
    public string? Detail { get; set; }
}

/// <summary>Tentative planning items painted over the planner before promotion (DATA-SCHEMA §2.6).</summary>
public sealed class ScenarioDraft : ISyncEntity
{
    public Guid Id { get; set; }
    public Guid? TripId { get; set; }
    public string Title { get; set; } = default!;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public Guid? PlaceId { get; set; }
    public DraftKind? Kind { get; set; }

    /// <summary>Free planning bag (linked fare overlay, alternatives) as a JSON column.</summary>
    public string? Notes { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}
