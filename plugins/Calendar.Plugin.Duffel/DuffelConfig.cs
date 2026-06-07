namespace Calendar.Plugin.Duffel;

/// <summary>
/// The Duffel plugin's per-account config (travel-fares-plugin.md §12), bound from the manifest's JSON
/// Schema via <c>IPluginHost.GetConfig&lt;T&gt;()</c>. <see cref="Environment"/> selects which token the broker
/// injects (test vs live) — Duffel uses one base URL and distinguishes worlds by token + the <c>live_mode</c>
/// flag on resources, not by host. The market/currency feed <see cref="DuffelPlugin.Coverage"/>.
/// </summary>
public sealed class DuffelConfig
{
    /// <summary>"test" (deterministic fixture routes, CI default) or "live". Drives the broker's token choice.</summary>
    public string Environment { get; set; } = "test";

    /// <summary>Default market the provider serves, e.g. "US" (travel-fares-plugin.md §8 currency normalization).</summary>
    public string Market { get; set; } = "US";

    /// <summary>Default display currency, e.g. "USD".</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>Stay search radius in km when the query doesn't carry one (travel-fares-plugin.md §5.1).</summary>
    public int RadiusKm { get; set; } = 5;
}
