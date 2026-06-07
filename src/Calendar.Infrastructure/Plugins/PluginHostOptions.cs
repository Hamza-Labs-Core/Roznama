namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Host configuration (PLUGIN-HOST.md §2.1): the ordered discovery directories and the default per-plugin
/// config JSON. Trust policy and resource budgets join this in later phases.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>
    /// Ordered plugin discovery roots. Each immediate subdirectory containing a <c>plugin.yaml</c> is a
    /// bundle. Earlier directories win on id collisions (in-box shadows user dir).
    /// </summary>
    public IList<string> Directories { get; set; } = new List<string>();
}
