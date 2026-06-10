using Microsoft.Extensions.Logging;

namespace Calendar.Plugin.Abstractions;

/// <summary>
/// The root contract for every plugin (assembly plugins implement it directly; declarative connectors
/// are surfaced as <see cref="IPlugin"/> implementations synthesized by the host's connector engine — §8).
/// </summary>
public interface IPlugin
{
    /// <summary>The plugin's manifest (id, version, capabilities, auth, network, config schema).</summary>
    PluginManifest Manifest { get; }

    /// <summary>
    /// Called once after load and before any capability method. The plugin captures the host services it
    /// needs (HttpClient, auth broker, cache, logger, config). It must not perform network I/O beyond what
    /// the egress allowlist permits, and must honor <paramref name="ct"/>.
    /// </summary>
    Task InitializeAsync(IPluginHost host, CancellationToken ct);
}

/// <summary>
/// Host services injected at <see cref="IPlugin.InitializeAsync"/>. Plugins never touch secrets or
/// arbitrary network — everything is mediated here (PLUGINS.md §1, §5).
/// </summary>
public interface IPluginHost
{
    /// <summary>
    /// An <see cref="HttpClient"/> whose egress is filtered to <c>manifest.network.allow</c>: a request to
    /// any other host fails before a byte leaves. Use this for ALL outbound HTTP. The handler applies the
    /// per-plugin Polly pipeline (retry/backoff/circuit-breaker/timeout) and resource budgets.
    /// </summary>
    HttpClient CreateClient();

    /// <summary>The auth broker — the plugin's only way to authenticate (§3). Returns scoped, short-lived handles; never raw secrets.</summary>
    IAuthBroker Auth { get; }

    /// <summary>A namespaced cache for the plugin (geocode results, sync snapshots, etc.). Subject to host eviction/budgets.</summary>
    IPluginCache Cache { get; }

    /// <summary>Structured logging. Host-controlled sink; included in the audit trail. Plugins must not log secrets.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// The plugin's configuration, deserialized from the account's settings and validated against
    /// <c>manifest.config</c> (JSON Schema). <typeparamref name="T"/> is a plugin-defined POCO whose shape
    /// matches the schema. Throws if validation failed at configure time.
    /// </summary>
    T GetConfig<T>() where T : class;
}

/// <summary>A small namespaced key/value cache handed to each plugin. Values are opaque blobs; TTL is advisory.</summary>
public interface IPluginCache
{
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, byte[] value, TimeSpan? ttl, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}

/// <summary>How a plugin is packaged and run (PLUGINS.md §2).</summary>
public enum PluginKind
{
    /// <summary>Manifest + OpenAPI + JSONata mapping. No code; runs in the shared connector engine. The default, safest tier.</summary>
    Declarative,

    /// <summary>Compiled C# implementing SDK interfaces; loaded into a collectible AssemblyLoadContext (or out-of-process).</summary>
    Assembly
}

/// <summary>The in-memory form of <c>plugin.yaml</c>. Every field maps 1:1 to the manifest spec (PLUGINS.md §3).</summary>
public sealed record PluginManifest(
    string Id,                                  // reverse-DNS unique id, e.g. "org.unifiedcalendar.google"
    string Name,
    string Version,                             // plugin semver (independent of SdkVersion)
    string SdkVersion,                          // SDK compatibility range, e.g. "1.x" — checked per §1
    PluginKind Kind,
    IReadOnlyList<string> Capabilities,         // capability ids from the catalog (§2.4); validated at load
    PluginPublisher? Publisher,                 // name + detached signature for trust-tier verification
    AuthSpec Auth,
    NetworkSpec Network,
    ConfigSchema Config);                       // JSON Schema driving the auto-generated settings form

/// <summary>Publisher identity for signature/trust-tier checks (PLUGINS.md §8).</summary>
public sealed record PluginPublisher(string Name, string? Signature);

/// <summary>
/// What the plugin needs to authenticate. The HOST runs the scheme; the plugin only declares it
/// (PLUGINS.md §3, §6). Endpoints may come from here or from the OpenAPI <c>securitySchemes</c>.
/// </summary>
public sealed record AuthSpec(
    AuthScheme Scheme,
    string? AuthorizationUrl = null,            // oauth2-pkce
    string? TokenUrl = null,                    // oauth2-*
    string? Authority = null,                   // e.g. Entra "…/common" (Microsoft plugin)
    IReadOnlyList<string>? Scopes = null,       // oauth2 scopes
    string? In = null,                          // apikey placement: "header" | "query"
    string? Name = null,                        // apikey header/param name, e.g. "Authorization", "X-Goog-Api-Key", "apikey"
    string? Format = null,                      // apikey value template, e.g. "Bearer {token}"
    IReadOnlyDictionary<string, string>? Params = null); // extra auth params, e.g. access_type=offline, prompt=consent

/// <summary>Network egress permission. The host's HttpClient physically cannot reach a host not listed here.</summary>
public sealed record NetworkSpec(
    IReadOnlyList<string> Allow);               // host patterns: "api.duffel.com", "*.caldav.icloud.com", "*"

/// <summary>
/// The plugin's config JSON Schema (object schema) plus the resolved defaults. Drives the schema-driven
/// settings UI (PLUGINS.md §9); validated before <see cref="IPlugin.InitializeAsync"/>.
/// </summary>
public sealed record ConfigSchema(
    string JsonSchema,                          // the raw JSON Schema document (type: object)
    IReadOnlyList<string> Required);            // required property names

/// <summary>Canonical capability id strings — the values that appear in <c>manifest.capabilities</c>.</summary>
public static class CapabilityIds
{
    public const string CalendarRead    = "calendar.read";
    public const string CalendarWrite   = "calendar.write";
    public const string ItineraryImport = "itinerary.import";
    public const string FlightPrice     = "flight.price";
    public const string StayPrice       = "stay.price";
    public const string GeoTiles        = "geo.tiles";
    public const string GeoGeocode      = "geo.geocode";
    public const string GeoRoute        = "geo.route";
    public const string GeoPlaces       = "geo.places";
    public const string Notify          = "notify";

    /// <summary>Map a strongly-typed <see cref="Capability"/> to its canonical id string.</summary>
    public static string For(Capability capability) => capability switch
    {
        Capability.CalendarRead    => CalendarRead,
        Capability.CalendarWrite   => CalendarWrite,
        Capability.ItineraryImport => ItineraryImport,
        Capability.FlightPrice     => FlightPrice,
        Capability.StayPrice       => StayPrice,
        Capability.GeoTiles        => GeoTiles,
        Capability.GeoGeocode      => GeoGeocode,
        Capability.GeoRoute        => GeoRoute,
        Capability.GeoPlaces       => GeoPlaces,
        Capability.Notify          => Notify,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
    };

    /// <summary>Parse a capability id string to its <see cref="Capability"/>; throws on an unknown id.</summary>
    public static Capability Parse(string id) => id switch
    {
        CalendarRead    => Capability.CalendarRead,
        CalendarWrite   => Capability.CalendarWrite,
        ItineraryImport => Capability.ItineraryImport,
        FlightPrice     => Capability.FlightPrice,
        StayPrice       => Capability.StayPrice,
        GeoTiles        => Capability.GeoTiles,
        GeoGeocode      => Capability.GeoGeocode,
        GeoRoute        => Capability.GeoRoute,
        GeoPlaces       => Capability.GeoPlaces,
        Notify          => Capability.Notify,
        _ => throw new ArgumentException($"Unknown capability id '{id}'.", nameof(id))
    };

    /// <summary>Try-parse a capability id string; false on an unknown id.</summary>
    public static bool TryParse(string id, out Capability capability)
    {
        switch (id)
        {
            case CalendarRead:    capability = Capability.CalendarRead;    return true;
            case CalendarWrite:   capability = Capability.CalendarWrite;   return true;
            case ItineraryImport: capability = Capability.ItineraryImport; return true;
            case FlightPrice:     capability = Capability.FlightPrice;     return true;
            case StayPrice:       capability = Capability.StayPrice;       return true;
            case GeoTiles:        capability = Capability.GeoTiles;        return true;
            case GeoGeocode:      capability = Capability.GeoGeocode;      return true;
            case GeoRoute:        capability = Capability.GeoRoute;        return true;
            case GeoPlaces:       capability = Capability.GeoPlaces;       return true;
            case Notify:          capability = Capability.Notify;          return true;
            default:              capability = default;                    return false;
        }
    }
}

/// <summary>Strongly-typed mirror of <see cref="CapabilityIds"/> for host-side dispatch.</summary>
public enum Capability
{
    CalendarRead,
    CalendarWrite,
    ItineraryImport,
    FlightPrice,
    StayPrice,
    GeoTiles,
    GeoGeocode,
    GeoRoute,
    GeoPlaces,
    Notify
}
