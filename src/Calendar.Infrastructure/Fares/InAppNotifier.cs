using Calendar.Application.Fares;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Calendar.Infrastructure.Fares;

/// <summary>
/// The persisted in-app notifier (ARCHITECTURE.md §14, travel-fares-plugin.md §10): every fired fare-drop /
/// target-cross event is written to <see cref="NotificationLog"/> so the UI and <c>GET /api/notifications</c>
/// can read it newest-first. This is the source of truth the feature hinges on; other channels are optional.
/// Also serves reads via <see cref="INotificationReader"/>.
/// </summary>
public sealed class InAppNotifier : IFareNotifier, INotificationReader
{
    private readonly CalendarDbContext _db;
    private readonly Func<DateTimeOffset> _now;

    public InAppNotifier(CalendarDbContext db) : this(db, () => DateTimeOffset.UtcNow) { }

    public InAppNotifier(CalendarDbContext db, Func<DateTimeOffset> now)
    {
        _db = db;
        _now = now;
    }

    /// <inheritdoc />
    public NotificationChannel Channel => NotificationChannel.InApp;

    /// <inheritdoc />
    public async Task NotifyAsync(FareDropNotification notification, CancellationToken ct)
    {
        var row = new NotificationLog
        {
            Id = Guid.CreateVersion7(),
            FareWatchId = notification.FareWatchId,
            Kind = notification.Kind,
            Channel = NotificationChannel.InApp,
            CreatedAtUtc = _now(),
            Message = notification.Message,
            Price = notification.Price,
            Currency = notification.Currency,
            PreviousPrice = notification.PreviousPrice,
            Source = notification.Source,
            Delivered = true, // an in-app log row is "delivered" the moment it is queryable.
        };
        _db.Notifications.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(int limit, CancellationToken ct)
    {
        var capped = Math.Clamp(limit, 1, 500);
        var rows = await _db.Notifications
            .AsNoTracking()
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Take(capped)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(n => new NotificationDto(
            n.Id, n.FareWatchId, n.Kind, n.Channel, n.CreatedAtUtc,
            n.Message, n.Price, n.Currency, n.PreviousPrice, n.Source, n.Delivered)).ToList();
    }
}
