namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>notify</c>. Delivers reminders/alerts (fare-drop, leave-by, event reminders) via email/push/webhook.</summary>
public interface INotifier : IPlugin
{
    /// <summary>Channels this notifier can deliver on — the host routes a message to a capable notifier.</summary>
    NotifyChannels Channels { get; }

    /// <summary>Send one notification. Returns when accepted for delivery (not necessarily delivered).</summary>
    Task NotifyAsync(Notification message, CancellationToken ct);
}

/// <summary>Delivery channels a notifier supports.</summary>
public sealed record NotifyChannels(bool Email, bool Push, bool Webhook);

/// <summary>Severity / intent hint for rendering and routing.</summary>
public enum NotifyKind { Reminder, FareDrop, LeaveBy, Conflict, SyncError, Info }

/// <summary>A notification to deliver. <see cref="DeepLink"/> opens the relevant calendar/trip/fare surface.</summary>
public sealed record Notification(
    NotifyKind Kind,
    string Title,
    string Body,
    string? DeepLink,
    DateTimeOffset CreatedAt);
