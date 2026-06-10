namespace Calendar.Plugin.CalDav;

/// <summary>
/// The CalDAV plugin's per-account config (caldav-plugin.md §12), bound from the manifest's JSON Schema via
/// <c>IPluginHost.GetConfig&lt;T&gt;()</c>. The credential (app-specific password) lives in the vault and is
/// applied by the broker — it is NEVER part of this config.
/// </summary>
public sealed class CalDavConfig
{
    /// <summary>The CalDAV base/server URL to seed discovery, e.g. <c>https://caldav.icloud.com</c>.</summary>
    public string ServerUrl { get; set; } = default!;

    /// <summary>The account username / Apple ID. The password is the vaulted app-specific password.</summary>
    public string Username { get; set; } = default!;
}
