using Calendar.Plugin.Abstractions;

namespace Calendar.Domain.Entities;

/// <summary>A calendar within an account: provider cache + user overrides (DATA-SCHEMA §2.3).</summary>
public sealed class Calendar : ISyncEntity
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Provider calendar id / CalDAV collection href / ICS feed URL — the sync target.</summary>
    public string RemoteId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Color { get; set; }

    /// <summary>User override — replicates. Default true.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>From accessRole/ACL/ICS (ICS always read-only). Gates write affordances.</summary>
    public bool IsReadOnly { get; set; }

    public CalendarKind? Kind { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// The central entity: provider-cached event, master + recurrence overrides (DATA-SCHEMA §2.3, §3).
/// Provider owns truth — no sync-metadata. Occurrences are never materialized; the master stores the RRULE.
/// </summary>
public sealed class Event
{
    public Guid Id { get; set; }
    public Guid CalendarId { get; set; }

    /// <summary>iCal UID / Google iCalUID — the stable cross-system id that feeds dedup.</summary>
    public string Uid { get; set; } = default!;

    /// <summary>Provider resource id (Google id / CalDAV href / ICS (UID,RECURRENCE-ID)); unique within calendar.</summary>
    public string RemoteId { get; set; } = default!;

    public string? Title { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public bool AllDay { get; set; }

    /// <summary>Set only when <see cref="AllDay"/> — the floating date, to avoid UTC-shift off-by-one bugs.</summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>Master RRULE (+ serialized RDATE/EXDATE). Null for non-recurring and for override rows.</summary>
    public string? Rrule { get; set; }

    /// <summary>Self-FK set on override rows (a moved/edited occurrence); null on masters and singletons.</summary>
    public Guid? MasterId { get; set; }

    /// <summary>The RECURRENCE-ID / Google originalStartTime — which occurrence this override replaces.</summary>
    public DateTimeOffset? RecurrenceId { get; set; }

    public Guid? PlaceId { get; set; }

    /// <summary>Raw free-form location text (kept even when geocoding fails, for retry).</summary>
    public string? Location { get; set; }

    /// <summary><c>hash(normalizedTitle | startDate | allDay | endDate?)</c> — the grouping key. Null until computed.</summary>
    public string? DedupSignature { get; set; }
    public Guid? DuplicateGroupId { get; set; }

    public EventStatus Status { get; set; }

    /// <summary>Provider concurrency token (Google etag, CalDAV ETag) for If-Match/412 write-back.</summary>
    public string? ETag { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }

    /// <summary>Local concurrency token (app-stamped on each write).</summary>
    public string RowVersion { get; set; } = default!;

    // Navigation
    public Calendar Calendar { get; set; } = default!;
    public Place? Place { get; set; }
    public Event? Master { get; set; }
    public ICollection<Event> Overrides { get; set; } = new List<Event>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
}

/// <summary>A category/tag with visibility (DATA-SCHEMA §2.3; ARCHITECTURE §13).</summary>
public sealed class Category : ISyncEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>One toggle hides all matching events.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Source-derived + user rules (keywords, calendar-of-origin, provider names) as a JSON bag.</summary>
    public string? MatchRules { get; set; }

    /// <summary>Seeded categories can't be deleted, only hidden.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>Event ↔ Category join (DATA-SCHEMA §2.3). Rule-derived rows are re-derivable; not sync-replicated.</summary>
public sealed class EventCategory
{
    public Guid EventId { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>So a user assignment survives re-evaluation of source rules.</summary>
    public AssignmentSource AssignedBy { get; set; }
}
