namespace Calendar.Domain.Entities;

/// <summary>A published read-only calendar/filter (DATA-SCHEMA §2.7; ARCHITECTURE §16).</summary>
public sealed class Share : ISyncEntity
{
    public Guid Id { get; set; }

    /// <summary>Null when the share is a filtered union (then <see cref="Filter"/> defines it).</summary>
    public Guid? CalendarId { get; set; }

    /// <summary>Filter spec for a union share (calendars[]/categories[]) as a JSON column.</summary>
    public string? Filter { get; set; }

    /// <summary>Unguessable capability token; the public feed/web URL path. Treated as a secret.</summary>
    public string Token { get; set; } = default!;
    public ShareScope Scope { get; set; }

    /// <summary>Null = no expiry; revocation = <see cref="ISyncEntity.IsDeleted"/> tombstone.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Per-account and per-calendar sync cursor (DATA-SCHEMA §2.8). Device-local — no sync-metadata.</summary>
public sealed class SyncState
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Null = account-level state; set = per-calendar token (Google/CalDAV hold a token per calendar).</summary>
    public Guid? CalendarId { get; set; }

    /// <summary>Provider delta cursor (opaque): Google nextSyncToken, CalDAV sync-token, ICS minted, Graph delta link.</summary>
    public string? SyncToken { get; set; }

    /// <summary>CalDAV CTag fallback (when sync-collection unsupported).</summary>
    public string? Ctag { get; set; }
    public DateTimeOffset? LastSyncAtUtc { get; set; }
    public DateTimeOffset? NextRunAtUtc { get; set; }
    public SyncRunState State { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? BackoffUntilUtc { get; set; }

    /// <summary>Consecutive failed sync attempts; drives the exponential backoff. Reset to 0 on success.</summary>
    public int Attempts { get; set; }
    public string RowVersion { get; set; } = default!;
}

/// <summary>Offline write-back queue (DATA-SCHEMA §2.8). Idempotent, ordered, retried. Device-local.</summary>
public sealed class WriteOutbox
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = default!;
    public Guid EntityId { get; set; }
    public OutboxOp Operation { get; set; }

    /// <summary>The mutation (partial event, patch) to replay through the plugin (JSON column).</summary>
    public string Payload { get; set; } = default!;

    /// <summary>The If-Match/ETag captured at queue time for optimistic write-back.</summary>
    public string? BaseETag { get; set; }
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset EnqueuedAtUtc { get; set; }
    public string RowVersion { get; set; } = default!;
}

/// <summary>Single-row install identity used to stamp the sync-metadata quartet (DATA-SCHEMA §7).</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
