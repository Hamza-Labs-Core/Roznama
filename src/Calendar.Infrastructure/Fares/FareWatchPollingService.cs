using Calendar.Application.Fares;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Fares;

/// <summary>
/// A Quartz-free hosted background poll (ARCHITECTURE.md §14, travel-fares-plugin.md §10). On an interval it
/// resolves a scoped <see cref="IFareWatchService"/> and runs <see cref="IFareWatchService.PollAsync"/> once,
/// appending samples + firing drop/target notifications. The interval is rate-budget-bounded (§11) and the loop
/// swallows faults so one bad sweep never tears down the host. Disabled by default; opt in via configuration.
/// </summary>
public sealed class FareWatchPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly FareWatchPollingOptions _options;
    private readonly ILogger<FareWatchPollingService> _logger;

    public FareWatchPollingService(
        IServiceScopeFactory scopes,
        IOptions<FareWatchPollingOptions> options,
        ILogger<FareWatchPollingService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Fare-watch polling is disabled (no interval configured).");
            return;
        }

        var interval = _options.Interval > TimeSpan.Zero ? _options.Interval : TimeSpan.FromHours(6);
        _logger.LogInformation("Fare-watch polling started (interval {Interval}).", interval);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IFareWatchService>();
                var summary = await service.PollAsync(stoppingToken).ConfigureAwait(false);
                if (summary.WatchesPolled > 0)
                    _logger.LogInformation(
                        "Fare-watch sweep: {Watches} polled, {Samples} sampled, {Notifications} notified.",
                        summary.WatchesPolled, summary.SamplesRecorded, summary.NotificationsFired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fare-watch sweep faulted; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Options for the hosted fare-watch poll (bound from the <c>FareWatchPolling</c> config section).</summary>
public sealed class FareWatchPollingOptions
{
    /// <summary>When false (default) the hosted loop runs once then exits — polls only happen via the manual trigger.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to sweep all active watches. Defaults to 6h when enabled (rate-budget bounded, §11).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}
