using Calendar.Application.Calendars;
using Calendar.Domain;
using Calendar.Domain.Entities;
using Calendar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// Event reminders (ROADMAP Phase 6 polish). The sweep fires a reminder when
/// <c>start − lead ≤ now ≤ start</c> by appending a <see cref="NotificationLog"/> row
/// (<see cref="NotificationKind.Reminder"/>, in-app channel) — the same log the notifications panel reads —
/// and quietly expires reminders whose event already started (e.g. the app was closed through the window).
/// Recurring masters fire off their first occurrence's start; per-occurrence reminders are a follow-up.
/// </summary>
public sealed class ReminderService : IReminderService
{
    private readonly CalendarDbContext _db;
    private readonly ILogger<ReminderService> _logger;
    private readonly IChangeFeed? _feed;

    public ReminderService(CalendarDbContext db, ILogger<ReminderService> logger, IChangeFeed? feed = null)
    {
        _db = db;
        _logger = logger;
        _feed = feed;
    }

    public async Task<ReminderDto?> CreateAsync(Guid eventId, int leadMinutes, CancellationToken ct)
    {
        if (leadMinutes < 0)
            throw new ArgumentException("leadMinutes must be ≥ 0.", nameof(leadMinutes));

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);
        if (ev is null)
            return null;

        var reminder = new Reminder
        {
            Id = Guid.CreateVersion7(),
            EventId = eventId,
            LeadMinutes = leadMinutes,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        _db.Reminders.Add(reminder);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReminderDto(reminder.Id, eventId, ev.Title ?? string.Empty, ev.StartUtc, leadMinutes, null);
    }

    public async Task<IReadOnlyList<ReminderDto>> ListAsync(CancellationToken ct) =>
        await (
            from r in _db.Reminders
            join e in _db.Events on r.EventId equals e.Id
            orderby e.StartUtc
            select new ReminderDto(r.Id, r.EventId, e.Title ?? string.Empty, e.StartUtc, r.LeadMinutes, r.FiredAtUtc))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var reminder = await _db.Reminders.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (reminder is null)
            return false;
        _db.Reminders.Remove(reminder);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<ReminderSweepSummary> SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Pending reminders with their event times; the lead arithmetic happens in memory (tiny set).
        var pending = await (
            from r in _db.Reminders
            where r.FiredAtUtc == null
            join e in _db.Events on r.EventId equals e.Id
            select new { Reminder = r, e.Title, e.StartUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        int fired = 0, expired = 0;
        foreach (var item in pending)
        {
            var fireAt = item.StartUtc - TimeSpan.FromMinutes(item.Reminder.LeadMinutes);
            if (now < fireAt)
                continue;                                   // not due yet.

            item.Reminder.FiredAtUtc = now;
            if (now <= item.StartUtc)
            {
                _db.Notifications.Add(new NotificationLog
                {
                    Id = Guid.CreateVersion7(),
                    Kind = NotificationKind.Reminder,
                    Channel = NotificationChannel.InApp,
                    CreatedAtUtc = now,
                    Message = $"Reminder: '{item.Title}' starts {item.StartUtc:yyyy-MM-dd HH:mm} UTC.",
                    Price = 0,
                    Currency = string.Empty,
                    Source = "reminder",
                    Delivered = true,
                });
                fired++;
            }
            else
            {
                expired++;                                  // missed the window — no late noise.
            }
        }

        if (fired + expired > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Reminder sweep: {Fired} fired, {Expired} expired.", fired, expired);
        }
        if (fired > 0)
            _feed?.Publish(ChangeEventTypes.NotificationsChanged, new { fired });
        return new ReminderSweepSummary(fired, expired);
    }
}

/// <summary>
/// The hosted ticker behind reminders: a cheap periodic <see cref="IReminderService.SweepAsync"/>. On by
/// default (reminders should just work); opt out via the <c>ReminderSweep</c> config section.
/// </summary>
public sealed class ReminderSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReminderSweepOptions _options;
    private readonly ILogger<ReminderSweepService> _logger;

    public ReminderSweepService(
        IServiceScopeFactory scopes, IOptions<ReminderSweepOptions> options, ILogger<ReminderSweepService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Reminder sweep is disabled.");
            return;
        }

        var tick = _options.TickInterval > TimeSpan.Zero ? _options.TickInterval : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IReminderService>()
                    .SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder sweep faulted; will retry next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Options for the hosted reminder sweep (bound from the <c>ReminderSweep</c> config section).</summary>
public sealed class ReminderSweepOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);
}
