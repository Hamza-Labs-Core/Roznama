using Calendar.Application.Cloud;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Calendar.Infrastructure.Cloud;

/// <summary>
/// The hosted ticker behind cloud sync: a periodic push+pull round while an enrollment exists (the tick is
/// a no-op when cloud sync is disabled). Faults are swallowed — connectivity blips never tear down the host.
/// </summary>
public sealed class CloudSyncPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly CloudSyncPollingOptions _options;
    private readonly ILogger<CloudSyncPollingService> _logger;

    public CloudSyncPollingService(
        IServiceScopeFactory scopes, IOptions<CloudSyncPollingOptions> options,
        ILogger<CloudSyncPollingService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Cloud-sync polling is disabled; rounds only run via POST /cloud/sync.");
            return;
        }

        var tick = _options.TickInterval > TimeSpan.Zero ? _options.TickInterval : TimeSpan.FromMinutes(5);
        using var timer = new PeriodicTimer(tick);
        do
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var cloud = scope.ServiceProvider.GetRequiredService<ICloudSyncService>();
                if ((await cloud.GetStatusAsync(stoppingToken).ConfigureAwait(false)).Enabled)
                    await cloud.SyncAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloud-sync round failed; will retry next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>Options for the hosted cloud-sync ticker (bound from the <c>CloudSyncPolling</c> config section).</summary>
public sealed class CloudSyncPollingOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(5);
}
