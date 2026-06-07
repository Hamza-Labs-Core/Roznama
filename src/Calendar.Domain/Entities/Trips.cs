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

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
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
