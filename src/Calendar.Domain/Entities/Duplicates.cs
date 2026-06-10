namespace Calendar.Domain.Entities;

/// <summary>A set of events sharing a signature (DATA-SCHEMA §2.4; ARCHITECTURE §12).</summary>
public sealed class DuplicateGroup : ISyncEntity
{
    public Guid Id { get; set; }
    public string Signature { get; set; } = default!;

    /// <summary>The event that shows; others are suppressed as "+N". Null until chosen.</summary>
    public Guid? CanonicalEventId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>User merge/split/never-merge metadata — reversible (DATA-SCHEMA §2.4; ARCHITECTURE §12).</summary>
public sealed class DuplicateOverride : ISyncEntity
{
    public Guid Id { get; set; }
    public OverrideKind Kind { get; set; }

    /// <summary>The events the override binds, stored by <c>Uid</c> (JSON array) so it survives provider re-sync.</summary>
    public string EventIds { get; set; } = default!;

    public string? Reason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}
