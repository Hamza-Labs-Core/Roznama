using Calendar.Domain;

namespace Calendar.Application.Calendars;

/// <summary>One copy inside a duplicate group, with enough provenance to choose between copies.</summary>
public sealed record DuplicateMemberDto(
    Guid EventId,
    string Uid,
    string Title,
    DateTimeOffset StartUtc,
    string CalendarName,
    string AccountName,
    bool IsCanonical);

/// <summary>A duplicate group as the inspector shows it (ARCHITECTURE §12).</summary>
public sealed record DuplicateGroupDto(Guid GroupId, IReadOnlyList<DuplicateMemberDto> Members);

/// <summary>A user merge/split/canonical override (reversible; bound by iCal UID).</summary>
public sealed record DuplicateOverrideDto(
    Guid Id,
    string Kind,
    IReadOnlyList<string> Uids,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// User control over duplicate grouping (the Phase 1 follow-up; ARCHITECTURE §12): inspect a group's
/// copies, then bend the automatic answer — split a copy out (<see cref="OverrideKind.NeverMerge"/>),
/// merge missed duplicates (<see cref="OverrideKind.ForceMerge"/>), or pick which copy shows
/// (<see cref="OverrideKind.SetCanonical"/>). Overrides tombstone on removal, restoring the automatic
/// grouping; every change re-runs the grouper immediately.
/// </summary>
public interface IDuplicateService
{
    /// <summary>The duplicate group containing <paramref name="eventId"/>, or null when it isn't grouped.</summary>
    Task<DuplicateGroupDto?> GetGroupForEventAsync(Guid eventId, CancellationToken ct);

    /// <summary>
    /// Create an override and regroup. Validation: <see cref="OverrideKind.ForceMerge"/> needs ≥ 2 UIDs,
    /// <see cref="OverrideKind.NeverMerge"/> ≥ 1, <see cref="OverrideKind.SetCanonical"/> exactly 1.
    /// </summary>
    Task<DuplicateOverrideDto> CreateOverrideAsync(
        OverrideKind kind, IReadOnlyList<string> uids, string? reason, CancellationToken ct);

    Task<IReadOnlyList<DuplicateOverrideDto>> ListOverridesAsync(CancellationToken ct);

    /// <summary>Tombstone an override and regroup (undo). False when unknown.</summary>
    Task<bool> RemoveOverrideAsync(Guid id, CancellationToken ct);
}
