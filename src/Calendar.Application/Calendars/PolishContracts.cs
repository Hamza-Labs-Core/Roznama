namespace Calendar.Application.Calendars;

// ── Reminders (ROADMAP Phase 6 polish) ──────────────────────────────────────────────────────────────

/// <summary>A pending or fired reminder, joined with its event for display.</summary>
public sealed record ReminderDto(
    Guid Id,
    Guid EventId,
    string EventTitle,
    DateTimeOffset EventStartUtc,
    int LeadMinutes,
    DateTimeOffset? FiredAtUtc);

/// <summary>Outcome of one reminder sweep: notifications written vs. reminders quietly expired.</summary>
public sealed record ReminderSweepSummary(int Fired, int Expired);

/// <summary>
/// Event reminders: created against an event, fired by a periodic sweep into the in-app
/// <c>NotificationLog</c> (<c>NotificationKind.Reminder</c>) the notifications panel already shows.
/// Device-local; a reminder whose event already started is expired silently.
/// </summary>
public interface IReminderService
{
    /// <summary>Create a reminder firing <paramref name="leadMinutes"/> before the event starts. Null = no such event.</summary>
    Task<ReminderDto?> CreateAsync(Guid eventId, int leadMinutes, CancellationToken ct);

    Task<IReadOnlyList<ReminderDto>> ListAsync(CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Fire all due reminders (start − lead ≤ now ≤ start) and expire missed ones.</summary>
    Task<ReminderSweepSummary> SweepAsync(CancellationToken ct);
}

// ── ICS import (ROADMAP Phase 6 polish) ─────────────────────────────────────────────────────────────

/// <summary>Outcome of an ICS import: the snapshot account/calendar created (or reused) and the event count.</summary>
public sealed record ImportResult(Guid AccountId, Guid CalendarId, int Events);

/// <summary>
/// One-shot ICS import: parses an iCalendar payload into a local read-only snapshot calendar (no feed URL,
/// no refresh — for files; live feeds use the ICS plugin). Re-importing under the same name upserts by UID.
/// </summary>
public interface IImportService
{
    Task<ImportResult> ImportIcsAsync(string? name, string ics, CancellationToken ct);
}

// ── Event search (ROADMAP Phase 6 polish) ───────────────────────────────────────────────────────────

/// <summary>One search hit for the toolbar search box.</summary>
public sealed record SearchResultDto(
    Guid Id,
    Guid CalendarId,
    string CalendarName,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location);

/// <summary>Substring search over stored events (title + location) on visible calendars.</summary>
public interface IEventSearchService
{
    Task<IReadOnlyList<SearchResultDto>> SearchAsync(string query, int limit, CancellationToken ct);
}
