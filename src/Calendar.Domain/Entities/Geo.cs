using Calendar.Plugin.Abstractions;

namespace Calendar.Domain.Entities;

/// <summary>
/// A resolved location pin (DATA-SCHEMA §2.5). A shared (referenced) entity — deduped by proximity+label so
/// one pin = one route leg = one map cluster.
/// </summary>
public sealed class Place : ISyncEntity
{
    public Guid Id { get; set; }
    public string Label { get; set; } = default!;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string? Address { get; set; }

    /// <summary>Winning geocoder id (nominatim/photon/google) for attribution provenance.</summary>
    public string? Source { get; set; }

    /// <summary>Proximity+label dedupe key (rounded lat,lng + lower-cased label) so equal places collapse.</summary>
    public string? NormalizedKey { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Query→coordinate cache (DATA-SCHEMA §2.5). Provider cache — no sync-metadata.</summary>
public sealed class GeocodeCache
{
    public Guid Id { get; set; }

    /// <summary>The normalized query (lower-cased, whitespace-collapsed, punctuation-stripped).</summary>
    public string Query { get; set; } = default!;

    /// <summary>SHA-256 of the normalized query (forward) or rounded lat,lng key (reverse). The lookup key.</summary>
    public string QueryHash { get; set; } = default!;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string Source { get; set; } = default!;
    public bool IsReverse { get; set; }
    public DateTimeOffset ResolvedAtUtc { get; set; }
}

/// <summary>Commute between two consecutive events (DATA-SCHEMA §2.5). Provider cache — no sync-metadata.</summary>
public sealed class RouteLeg
{
    public Guid Id { get; set; }
    public Guid FromEventId { get; set; }
    public Guid ToEventId { get; set; }
    public TravelMode Mode { get; set; }
    public int DurationSec { get; set; }

    /// <summary>Host-computed: <c>arriveBy − DurationSec − buffer</c>.</summary>
    public DateTimeOffset? LeaveByUtc { get; set; }

    /// <summary>Host-computed: <c>gap ≥ DurationSec</c>. False ⇒ "you can't make it".</summary>
    public bool Feasible { get; set; }

    /// <summary>Encoded polyline (precision 5) for the map leg line.</summary>
    public string? Geometry { get; set; }
    public string Source { get; set; } = default!;
    public DateTimeOffset ComputedAtUtc { get; set; }

    /// <summary>True when served as last-known after all providers failed.</summary>
    public bool IsStale { get; set; }
}
