namespace Calendar.Domain.Entities;

/// <summary>
/// A per-event reminder (ROADMAP Phase 6 polish). Device-local — like <see cref="SyncState"/>, reminders
/// fire on the device that set them, so no sync-metadata quartet. The sweep marks <see cref="FiredAtUtc"/>
/// and appends a <see cref="NotificationLog"/> row (<c>NotificationKind.Reminder</c>) the in-app
/// notification panel already surfaces.
/// </summary>
public sealed class Reminder
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }

    /// <summary>How long before the event's start the reminder fires.</summary>
    public int LeadMinutes { get; set; }

    /// <summary>Set when the sweep fired (or expired) the reminder; null = pending.</summary>
    public DateTimeOffset? FiredAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
