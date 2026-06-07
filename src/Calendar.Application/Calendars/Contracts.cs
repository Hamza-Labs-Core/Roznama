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

/// <summary>
/// Projects the stored events for a window into render-ready instances: expands recurrence, applies the
/// visibility pipeline, and collapses duplicates to their canonical (ARCHITECTURE §6, §12, §13).
/// </summary>
public interface IEventProjectionService
{
    Task<IReadOnlyList<ProjectedEvent>> GetEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
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
