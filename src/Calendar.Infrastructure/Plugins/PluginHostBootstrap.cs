using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Runs the plugin lifecycle once at application start (PLUGIN-HOST.md §2.3). Discovery never aborts host
/// startup — each plugin fails closed independently.
/// </summary>
public sealed class PluginHostBootstrap : IHostedService
{
    private readonly PluginHost _host;
    private readonly ILogger<PluginHostBootstrap> _logger;

    public PluginHostBootstrap(PluginHost host, ILogger<PluginHostBootstrap> logger)
    {
        _host = host;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _host.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure in discovery itself (not a single plugin) must not crash the app.
            _logger.LogError(ex, "Plugin host bootstrap encountered an unexpected error.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
