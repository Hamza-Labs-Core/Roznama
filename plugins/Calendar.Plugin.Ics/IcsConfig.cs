namespace Calendar.Plugin.Ics;

/// <summary>
/// The ICS plugin's per-account config (ics-plugin.md §15), bound from the manifest's JSON Schema via
/// <c>IPluginHost.GetConfig&lt;T&gt;()</c>. The feed URL is treated as a secret (the token is the credential).
/// </summary>
public sealed class IcsConfig
{
    /// <summary>The feed URL. <c>https://</c>, or <c>webcal://</c> (auto-rewritten to https).</summary>
    public string FeedUrl { get; set; } = default!;

    /// <summary>Poll cadence in minutes (floor 15). Default 60.</summary>
    public int RefreshMinutes { get; set; } = 60;

    /// <summary>Optional preset override for the calendar's display name.</summary>
    public string? CalendarName { get; set; }

    /// <summary>Optional preset category forced on every event (TripIt → Travel — ics-plugin.md §12).</summary>
    public string? ForceCategory { get; set; }
}
