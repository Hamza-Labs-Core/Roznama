using Calendar.Domain;

namespace Calendar.Application.Fares;

/// <summary>
/// A minimal host-side notification delivery channel (ARCHITECTURE.md §14, travel-fares-plugin.md §10). Distinct
/// from the SDK <c>notify</c> plugin capability (<c>Calendar.Plugin.Abstractions.INotifier</c>): this is the
/// internal sink the fare-watch poll writes to. The in-app notifier persists to the <c>NotificationLog</c> (the
/// queryable source of truth); email/webhook notifiers may be stubs that simply mark the log row delivered. The
/// poll calls <see cref="NotifyAsync"/> once per fired event; the implementation decides how (and whether) to
/// deliver beyond the persisted record.
/// </summary>
public interface IFareNotifier
{
    /// <summary>The channel this notifier delivers on (for the persisted log row).</summary>
    NotificationChannel Channel { get; }

    /// <summary>Deliver/record a notification. Implementations must not throw on transient delivery failure.</summary>
    Task NotifyAsync(FareDropNotification notification, CancellationToken ct);
}

/// <summary>The payload describing a fired fare-drop / target-cross event.</summary>
public sealed record FareDropNotification(
    Guid FareWatchId,
    NotificationKind Kind,
    string Message,
    decimal Price,
    string Currency,
    decimal? PreviousPrice,
    string? Source);

/// <summary>Reads the persisted in-app notification log for the UI / <c>GET /api/notifications</c>.</summary>
public interface INotificationReader
{
    /// <summary>The recorded notifications, newest-first.</summary>
    Task<IReadOnlyList<NotificationDto>> ListAsync(int limit, CancellationToken ct);
}

/// <summary>A notification as returned to the API.</summary>
public sealed record NotificationDto(
    Guid Id, Guid? FareWatchId,
    NotificationKind Kind, NotificationChannel Channel,
    DateTimeOffset CreatedAtUtc,
    string Message, decimal Price, string Currency,
    decimal? PreviousPrice, string? Source, bool Delivered);
