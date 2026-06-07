namespace Calendar.Plugin.Google;

/// <summary>
/// The Google Calendar plugin's per-account config (google-calendar-plugin.md §14), bound from the manifest's
/// JSON Schema via <c>IPluginHost.GetConfig&lt;T&gt;()</c>. Auth carries the account identity, so config is
/// just the initial full-sync window — once a <c>syncToken</c> exists the window is fixed (google §5 quirk).
/// </summary>
public sealed class GoogleConfig
{
    /// <summary>How many months in the past the initial full sync covers (default 12).</summary>
    public int SyncWindowMonthsPast { get; set; } = 12;

    /// <summary>How many months in the future the initial full sync covers (default 24).</summary>
    public int SyncWindowMonthsFuture { get; set; } = 24;
}
