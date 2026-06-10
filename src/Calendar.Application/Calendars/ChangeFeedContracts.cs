namespace Calendar.Application.Calendars;

/// <summary>One server-push event: an SSE event name + its JSON data payload (API.md "GET /sync/stream").</summary>
public sealed record ChangeEvent(string Type, string Json);

/// <summary>The change-event names the stream carries (UI.md §9 live refresh).</summary>
public static class ChangeEventTypes
{
    /// <summary>The stored event set changed (sync delta, write-back, import, cloud apply) — re-project views.</summary>
    public const string EventsChanged = "eventsChanged";

    /// <summary>An account's sync state transitioned (running/idle/backoff) — refresh the sidebar health.</summary>
    public const string SyncProgress = "syncProgress";

    /// <summary>The in-app notification log grew (reminders, fare drops) — refresh badges/panels.</summary>
    public const string NotificationsChanged = "notificationsChanged";
}

/// <summary>
/// The in-process pub/sub behind <c>GET /sync/stream</c> (ARCHITECTURE; UI.md §9): background engines
/// publish, every open SSE connection listens. Fan-out is fire-and-forget with per-subscriber bounded
/// buffers — a slow client drops oldest events (the UI treats any event as "reload", so drops are harmless)
/// and a publish never blocks an engine.
/// </summary>
public interface IChangeFeed
{
    /// <summary>Broadcast to all current listeners. <paramref name="payload"/> is JSON-serialized as the data line.</summary>
    void Publish(string type, object? payload = null);

    /// <summary>An endless stream of events for one subscriber; completes when <paramref name="ct"/> fires.</summary>
    IAsyncEnumerable<ChangeEvent> ListenAsync(CancellationToken ct);
}
