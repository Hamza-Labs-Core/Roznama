namespace Calendar.Application.Calendars;

/// <summary>
/// A create/update draft for a write through <c>calendar.write</c> (ARCHITECTURE §8; SDK-CONTRACT §4.write).
/// Times are UTC; all-day uses the date components. Only writable calendars (<c>Calendar.IsReadOnly == false</c>)
/// accept these.
/// </summary>
public sealed record EventWriteRequest(
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location,
    string? Rrule,
    IReadOnlyList<string>? Categories);

/// <summary>The outcome a write returns to the UI so it can reflect the change optimistically (ARCHITECTURE §8).</summary>
public sealed record EventWriteResult(
    Guid EventId,
    Guid CalendarId,
    WriteDisposition Disposition,
    Guid? OutboxId);

/// <summary>
/// Whether a write reached the provider or was queued offline (ARCHITECTURE §8/§10). <see cref="Applied"/> —
/// the plugin accepted it and the local row mirrors the provider. <see cref="Queued"/> — the provider was
/// unreachable, the mutation is in the <c>WriteOutbox</c> and the local row reflects it optimistically.
/// </summary>
public enum WriteDisposition { Applied, Queued }

/// <summary>A queued outbox row projected for <c>GET /sync/outbox</c> (ARCHITECTURE §8).</summary>
public sealed record OutboxItemDto(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Operation,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset EnqueuedAtUtc);

/// <summary>Queue health for <c>GET /sync/outbox</c>: total + per-status counts plus the queued rows.</summary>
public sealed record OutboxStatusDto(
    int Total,
    int Pending,
    int InFlight,
    int Failed,
    int Conflict,
    int Done,
    IReadOnlyList<OutboxItemDto> Items);

/// <summary>Replay outcome: how many queued writes drained, conflicted, or stayed pending (ARCHITECTURE §8/§10).</summary>
public sealed record ReplaySummary(int Drained, int Conflicts, int StillPending);

/// <summary>
/// Thrown when a write targets a read-only calendar (ICS feed, Holidays/Birthdays, reader-role) — surfaced as
/// a 409/forbidden (ARCHITECTURE §8). Read affordances are gated on <c>Calendar.IsReadOnly</c>.
/// </summary>
public sealed class ReadOnlyCalendarException : Exception
{
    public ReadOnlyCalendarException(string? message = null) : base(message) { }
}

/// <summary>
/// Writes events through the owning calendar's <c>calendar.write</c> plugin, queuing to the offline
/// <c>WriteOutbox</c> when the provider is unreachable and replaying it FIFO when reachable
/// (ARCHITECTURE §8/§10; SDK-CONTRACT §4.write). Every write reflects locally so the UI updates immediately.
/// </summary>
public interface IWriteService
{
    /// <summary>Create a draft event on a writable calendar; applies through the plugin or queues it offline.</summary>
    Task<EventWriteResult> CreateEventAsync(Guid calendarId, EventWriteRequest request, CancellationToken ct);

    /// <summary>Update an existing local event (If-Match by its provider ETag); applies through the plugin or queues.</summary>
    Task<EventWriteResult> UpdateEventAsync(Guid eventId, EventWriteRequest request, CancellationToken ct);

    /// <summary>Delete a local event through the plugin (If-Match), or queue the deletion offline.</summary>
    Task<EventWriteResult> DeleteEventAsync(Guid eventId, CancellationToken ct);

    /// <summary>Drain the <c>WriteOutbox</c> Pending rows in FIFO order; honors If-Match (412 → Conflict).</summary>
    Task<ReplaySummary> ReplayAsync(CancellationToken ct);

    /// <summary>Queued count + per-status breakdown for <c>GET /sync/outbox</c>.</summary>
    Task<OutboxStatusDto> GetOutboxStatusAsync(CancellationToken ct);
}
