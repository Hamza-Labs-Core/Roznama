using Calendar.Domain;

namespace Calendar.Application.Calendars;

/// <summary>A projected, render-ready event instance (API.md "events — the projected stream").</summary>
public sealed record ProjectedEvent(
    Guid Id,
    Guid CalendarId,
    string CalendarName,
    string? Color,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location,
    bool IsRecurringInstance,
    Guid? MasterId,
    int DuplicateCount,
    IReadOnlyList<string> Categories);

/// <summary>Outcome of one account sync round.</summary>
public sealed record SyncSummary(int Calendars, int Upserts, int Deletes);

/// <summary>Sidebar account row.</summary>
public sealed record AccountDto(Guid Id, string PluginId, string DisplayName, string Status, DateTimeOffset? LastSyncAtUtc);

/// <summary>Sidebar calendar row.</summary>
public sealed record CalendarDto(Guid Id, Guid AccountId, string Name, string? Color, bool IsVisible, bool IsReadOnly);

/// <summary>Sidebar category row.</summary>
public sealed record CategoryDto(Guid Id, string Name, bool IsVisible, bool IsBuiltIn);

/// <summary>Reads/normalizes provider events into the local store via the plugin host (ARCHITECTURE §10).</summary>
public interface ICalendarSyncService
{
    Task<SyncSummary> SyncAccountAsync(Guid accountId, CancellationToken ct);
}

/// <summary>Outcome of one scheduler sweep over all syncable accounts.</summary>
public sealed record SyncSweepSummary(int Accounts, int Synced, int Failed, int Skipped);

/// <summary>One account's scheduling/health row (backs <c>GET /sync/status</c>).</summary>
public sealed record SyncStatusDto(
    Guid AccountId,
    string DisplayName,
    string PluginId,
    string AccountStatus,
    string State,
    DateTimeOffset? LastSyncAtUtc,
    DateTimeOffset? NextRunAtUtc,
    DateTimeOffset? BackoffUntilUtc,
    int Attempts,
    string? LastError);

/// <summary>
/// The scheduled background sync engine (ROADMAP Phase 3): decides which accounts are due, runs them through
/// <see cref="ICalendarSyncService"/>, and records the per-account cadence + exponential backoff in the
/// account-level <c>SyncState</c> row (NextRunAtUtc / BackoffUntilUtc / Attempts).
/// </summary>
public interface ISyncScheduler
{
    /// <summary>One pass over all connected accounts; syncs the due ones. <paramref name="force"/> ignores due/backoff.</summary>
    Task<SyncSweepSummary> SweepAsync(bool force, CancellationToken ct);

    /// <summary>
    /// Syncs one account immediately (ignores due/backoff) with full scheduler bookkeeping. Returns null when
    /// the account doesn't exist; a failed sync records backoff and rethrows.
    /// </summary>
    Task<SyncSummary?> SyncNowAsync(Guid accountId, CancellationToken ct);

    /// <summary>Per-account scheduling state for the UI/API (last sync, next run, backoff, last error).</summary>
    Task<IReadOnlyList<SyncStatusDto>> GetStatusAsync(CancellationToken ct);
}

/// <summary>
/// Projects the stored events for a window into render-ready instances: expands recurrence, applies the
/// visibility pipeline, and collapses duplicates to their canonical (ARCHITECTURE §6, §12, §13).
/// </summary>
public interface IEventProjectionService
{
    Task<IReadOnlyList<ProjectedEvent>> GetEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Projects events for a share (ARCHITECTURE §16): restricted to the given calendars (regardless of the
    /// owner's UI visibility toggle — a hidden calendar can still be published), optionally narrowed to events
    /// carrying one of <paramref name="categoryIds"/>. Recurrence/override/dedup handling matches the main feed.
    /// </summary>
    Task<IReadOnlyList<ProjectedEvent>> GetEventsForShareAsync(
        IReadOnlyCollection<Guid> calendarIds,
        IReadOnlyCollection<Guid> categoryIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}

/// <summary>Connects and lists accounts. ICS connect needs no OAuth — just a feed URL.</summary>
public interface IAccountService
{
    Task<Guid> ConnectIcsAsync(string feedUrl, string? name, int refreshMinutes, string? forceCategory, CancellationToken ct);
    Task<IReadOnlyList<AccountDto>> ListAsync(CancellationToken ct);
}

/// <summary>Reads/writes calendars + categories (visibility toggles).</summary>
public interface ICalendarCatalog
{
    Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(CancellationToken ct);
    Task<bool> SetCalendarVisibilityAsync(Guid calendarId, bool isVisible, CancellationToken ct);
    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct);
    Task<bool> SetCategoryVisibilityAsync(Guid categoryId, bool isVisible, CancellationToken ct);
}

/// <summary>The encrypted-at-rest credential vault (Phase 1: stored opaque; AEAD/SQLCipher layer comes later).</summary>
public interface ISecretVault
{
    Task<Guid> StoreAsync(SecretKind kind, string value, CancellationToken ct);
    Task<string?> ReadAsync(Guid id, CancellationToken ct);
}
