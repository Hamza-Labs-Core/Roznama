using Calendar.Application.Calendars;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Calendars;

/// <summary>
/// The hosted ticker behind the scheduled sync engine (ROADMAP Phase 3). On a short tick it resolves a scoped
/// <see cref="ISyncScheduler"/> and sweeps once — the sweep itself decides which accounts are due from their
/// account-level SyncState (cadence + per-account exponential backoff), so the tick stays cheap. The loop
/// swallows faults so one bad sweep never tears down the host. On by default; opt out via configuration.
/// </summary>
public sealed class CalendarSyncPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CalendarSyncPollingOptions _options;
    private readonly ILogger<CalendarSyncPollingService> _logger;

    public CalendarSyncPollingService(
        IServiceScopeFactory scopes,
        IOptions<CalendarSyncPollingOptions> options,
        ILogger<CalendarSyncPollingService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Calendar-sync polling is disabled; syncs only run on connect or manual trigger.");
            return;
        }

        var tick = _options.TickInterval > TimeSpan.Zero ? _options.TickInterval : TimeSpan.FromMinutes(1);
        _logger.LogInformation("Calendar-sync polling started (tick {Tick}).", tick);

        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<ISyncScheduler>();
                var summary = await scheduler.SweepAsync(force: false, stoppingToken).ConfigureAwait(false);
                if (summary.Synced > 0 || summary.Failed > 0)
                    _logger.LogInformation(
                        "Sync sweep: {Synced} synced, {Failed} failed, {Skipped} not due (of {Accounts}).",
                        summary.Synced, summary.Failed, summary.Skipped, summary.Accounts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync sweep faulted; will retry next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Options for the hosted sync ticker (bound from the <c>CalendarSyncPolling</c> config section).</summary>
public sealed class CalendarSyncPollingOptions
{
    /// <summary>On by default — periodic refresh is core behavior; set false to sync only on connect/manual trigger.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep checks for due accounts (the per-account cadence is SyncScheduler:Interval).</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);
}
