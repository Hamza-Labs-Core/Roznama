namespace Calendar.Plugin.Microsoft;

/// <summary>
/// The Microsoft Graph plugin's per-account config (microsoft-graph-plugin.md §13), bound from the manifest's
/// JSON Schema via <c>IPluginHost.GetConfig&lt;T&gt;()</c>. Auth carries the account identity, so config is
/// just the initial <c>calendarView</c> delta window — once a <c>@odata.deltaLink</c> exists the window is
/// fixed (the deltaLink encodes it; widening it requires a fresh baseline — microsoft-graph-plugin.md §6).
/// </summary>
public sealed class MicrosoftConfig
{
    /// <summary>How many months in the past the initial delta window covers (default 1, floor 0).</summary>
    public int SyncWindowMonthsBack { get; set; } = 1;

    /// <summary>How many months in the future the initial delta window covers (default 12, floor 1).</summary>
    public int SyncWindowMonthsForward { get; set; } = 12;
}
