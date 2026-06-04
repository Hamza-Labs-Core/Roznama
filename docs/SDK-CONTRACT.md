# SDK contract — `Calendar.Plugin.Abstractions`

**Status: canonical.** This document is the **single source of truth** for the plugin SDK contract that
every plugin and the host compile against. The assembly `Calendar.Plugin.Abstractions`
([ARCHITECTURE.md §18](ARCHITECTURE.md#18-solution-layout)) is the *only* shared type boundary between host
and plugin; nothing else may cross it. Where the prose deep-dives
([docs/deep-dives/](deep-dives/)) and the system docs ([PLUGINS.md](PLUGINS.md),
[ARCHITECTURE.md](ARCHITECTURE.md), [API.md](API.md)) sketched a type, **this file pins its exact
shape.** Any divergence is reconciled here and flagged in [§ Reconciliation notes](#reconciliation-notes).

The code blocks below are the contract surface presented as an annotated C# header. They are normative:
member names, signatures, nullability, and enum members are the contract. They compile as a unit
(`net9.0`, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`). The assembly takes a
dependency only on the BCL plus `Microsoft.Extensions.Logging.Abstractions` (for `ILogger`) and
`System.Net.Http`.

- [1. Versioning & compatibility policy](#1-versioning--compatibility-policy)
- [2. Core: plugin, host, manifest, capability catalog](#2-core-plugin-host-manifest-capability-catalog)
- [3. Auth broker](#3-auth-broker)
- [4. Calendar capabilities](#4-calendar-capabilities)
- [5. Itinerary & pricing](#5-itinerary--pricing)
- [6. Geo capabilities](#6-geo-capabilities)
- [7. Notifications](#7-notifications)
- [8. Declarative connectors satisfy the same interfaces](#8-declarative-connectors-satisfy-the-same-interfaces)
- [9. Capability → interface index](#9-capability--interface-index)
- [Reconciliation notes](#reconciliation-notes)

---

## 1. Versioning & compatibility policy

The SDK assembly is **semver'd** and is the compatibility anchor for the whole plugin ecosystem
([PLUGINS.md §10](PLUGINS.md#10-distribution--versioning)).

- **`MAJOR.MINOR.PATCH`** applies to `Calendar.Plugin.Abstractions` as a whole.
- **Additive-only within a major.** Within a major version the contract changes are **strictly additive**:
  new capability interfaces, new optional members on a host service, new enum members, new records, new
  *optional* (defaulted/nullable) fields. Adding a capability = adding an interface; existing plugins are
  unaffected because they simply don't implement it ([PLUGINS.md §5](PLUGINS.md#5-assembly-plugins--the-sdk-contract)).
- **Never within a major:** removing/renaming a member, changing a signature, changing a field's type or
  nullability, reordering positional-record parameters, removing an enum member, or tightening a contract.
  Any of those bumps the **major** and plugins opt in explicitly.
- **A new capability interface is a MINOR bump**, not major — it cannot break an existing plugin.
- **PATCH** is doc/attribute/XML-comment changes with zero surface change.

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>
/// The SDK contract version this build of <c>Calendar.Plugin.Abstractions</c> exposes.
/// Compared against each plugin manifest's <see cref="PluginManifest.SdkVersion"/> at load time.
/// </summary>
public static class SdkVersion
{
    /// <summary>Current contract version (semver). Bumped per the policy in §1.</summary>
    public const string Current = "1.0.0";

    /// <summary>Current major. A plugin requesting a different major is rejected at load.</summary>
    public const int Major = 1;

    /// <summary>
    /// True if a plugin built against <paramref name="requested"/> (e.g. <c>"1.x"</c>, <c>"1.2"</c>,
    /// <c>"1.2.0"</c>) is loadable against this SDK: same major, and requested minor ≤ current minor
    /// (additive-only guarantees forward source-compat within the major).
    /// </summary>
    public static bool IsCompatible(string requested) =>
        SdkVersionRange.Parse(requested).Allows(Current);
}
```

**How `sdkVersion` is checked.** Every manifest declares a compatibility range as `sdkVersion`
(e.g. `"1.x"`, `"1.2"`, or an exact `"1.2.0"` — see every deep-dive manifest, which all pin `sdkVersion: "1.x"`).
The host parses it and, **before load**, rejects any plugin whose range does not admit `SdkVersion.Current`.
This check runs in the `validate` step of the lifecycle (`discover → validate → load → register → configure
→ run`, [PLUGINS.md §7](PLUGINS.md#7-loading-isolation--lifecycle)) alongside signature and permission checks.

```csharp
/// <summary>Parsed semver compatibility range from a manifest's <c>sdkVersion</c> field.</summary>
public readonly record struct SdkVersionRange(int Major, int? Minor)
{
    /// <summary>Parses <c>"1.x"</c> (major-only), <c>"1.2"</c> (major+minor), or <c>"1.2.0"</c> (exact → major+minor).</summary>
    public static SdkVersionRange Parse(string range) => SdkVersionRangeParser.Parse(range);

    /// <summary>True if <paramref name="version"/> satisfies this range (same major; minor ≥ requested when pinned).</summary>
    public bool Allows(string version) => SdkVersionRangeParser.Allows(this, version);
}
```

---

## 2. Core: plugin, host, manifest, capability catalog

### 2.1 `IPlugin` and the capability marker

Every plugin implements `IPlugin`; capability interfaces (§4–§7) derive from it. A plugin may implement
**several** capability interfaces (e.g. a geo provider that does both `geo.geocode` and `geo.places`).

```csharp
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
```

### 2.2 `IPluginHost` — the only services a plugin is given

A plugin touches the outside world **only** through `IPluginHost`. There is no ambient `HttpClient`, no
secret store, no file system. This is the structural sandbox ([PLUGINS.md §8](PLUGINS.md#8-security--sandboxing)).

```csharp
/// <summary>
/// Host services injected at <see cref="IPlugin.InitializeAsync"/>. Plugins never touch secrets or
/// arbitrary network — everything is mediated here ([PLUGINS.md §1, §5]).
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
```

```csharp
/// <summary>A small namespaced key/value cache handed to each plugin. Values are opaque blobs; TTL is advisory.</summary>
public interface IPluginCache
{
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, byte[] value, TimeSpan? ttl, CancellationToken ct);
    Task RemoveAsync(string key, CancellationToken ct);
}
```

> `ILogger` is `Microsoft.Extensions.Logging.ILogger` (the non-generic interface) — the one BCL-adjacent
> dependency the SDK takes, so host and plugins share one logging abstraction.

### 2.3 Manifest types

The manifest is authored as `plugin.yaml` ([PLUGINS.md §3](PLUGINS.md#3-manifest-specification)) and
deserialized by the host into `PluginManifest`. Plugins receive it back via `IPlugin.Manifest`. These
records are the canonical in-memory shape of every manifest field referenced across the deep-dives.

```csharp
/// <summary>How a plugin is packaged and run ([PLUGINS.md §2]).</summary>
public enum PluginKind
{
    /// <summary>Manifest + OpenAPI + JSONata mapping. No code; runs in the shared connector engine. The default, safest tier.</summary>
    Declarative,

    /// <summary>Compiled C# implementing SDK interfaces; loaded into a collectible AssemblyLoadContext (or out-of-process).</summary>
    Assembly
}

/// <summary>The in-memory form of <c>plugin.yaml</c>. Every field maps 1:1 to the manifest spec ([PLUGINS.md §3]).</summary>
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

/// <summary>Publisher identity for signature/trust-tier checks ([PLUGINS.md §8]).</summary>
public sealed record PluginPublisher(string Name, string? Signature);

/// <summary>
/// What the plugin needs to authenticate. The HOST runs the scheme; the plugin only declares it
/// ([PLUGINS.md §3, §6]). Endpoints may come from here or from the OpenAPI <c>securitySchemes</c>.
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
/// settings UI ([PLUGINS.md §9]); validated before <see cref="IPlugin.InitializeAsync"/>.
/// </summary>
public sealed record ConfigSchema(
    string JsonSchema,                          // the raw JSON Schema document (type: object)
    IReadOnlyList<string> Required);            // required property names
```

### 2.4 Capability id catalog

The capability ids are the contract the registry keys on (`capability → [plugins]`). They match
[ARCHITECTURE.md §5](ARCHITECTURE.md#5-capability-catalog) exactly. Both a string-constant form (used in
manifests/JSON) and an enum (used in host code) are canonical; `CapabilityIds.For`/`Parse` bridge them.

```csharp
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
```

Capabilities whose providers return **interchangeable results for the same query** —
`flight.price`, `stay.price`, `geo.route`, `geo.geocode` — automatically run through the
[fallback aggregator](ARCHITECTURE.md#15-multi-provider-fallback-aggregator). `geo.tiles` is a *single
chosen surface*, **not** aggregated ([geo-tiles-plugin.md §2](deep-dives/geo-tiles-plugin.md)).

---

## 3. Auth broker

A single host component runs **all** authentication so plugins never see client secrets or stored tokens
([PLUGINS.md §6](PLUGINS.md#6-authentication-broker)). The plugin asks the broker for a **scoped,
short-lived handle** at call time; the broker performs/refreshes the OAuth dance and keeps the refresh and
access tokens in the encrypted vault. A plugin holds the handle, applies it to a request, and discards it.

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>Authentication schemes the host can run on a plugin's behalf ([PLUGINS.md §6]).</summary>
public enum AuthScheme
{
    /// <summary>No credential (public ICS feeds, self-hosted geo). The plugin still gets no raw network beyond the allowlist.</summary>
    None,

    /// <summary>API key / bearer token injected as a header or query param per <see cref="AuthSpec.In"/>/<see cref="AuthSpec.Name"/>/<see cref="AuthSpec.Format"/>.</summary>
    ApiKey,

    /// <summary>HTTP Basic over TLS (username + password).</summary>
    Basic,

    /// <summary>Provider app-specific password over Basic (iCloud/Fastmail/Nextcloud CalDAV).</summary>
    AppPassword,

    /// <summary>OAuth 2.0 Authorization Code + PKCE (Google, Microsoft).</summary>
    OAuth2Pkce,

    /// <summary>OAuth 2.0 Client Credentials (machine-to-machine).</summary>
    OAuth2ClientCredentials
}
```

```csharp
/// <summary>
/// The plugin's sole authentication entry point. Never exposes client secrets, refresh tokens, or the raw
/// vaulted access token — only a scoped, short-lived <see cref="AuthHandle"/> the plugin applies per call.
/// </summary>
public interface IAuthBroker
{
    /// <summary>
    /// Get a fresh handle for the requested <paramref name="scopes"/> (a subset of the manifest's declared
    /// scopes). The broker refreshes silently; the handle may be valid for only minutes. Callers should
    /// fetch a handle immediately before use and not cache it across calls.
    /// </summary>
    Task<AuthHandle> GetTokenAsync(IReadOnlyList<string>? scopes, CancellationToken ct);

    /// <summary>
    /// Convenience: apply the current credential to <paramref name="request"/> per the manifest's
    /// <see cref="AuthScheme"/> (sets Authorization/Basic/apikey header or query param). Preferred over
    /// reading the raw token, so the plugin never handles the secret material directly.
    /// </summary>
    Task ApplyAsync(HttpRequestMessage request, IReadOnlyList<string>? scopes, CancellationToken ct);
}

/// <summary>
/// A scoped, short-lived authentication handle. For bearer/oauth schemes <see cref="Token"/> is an opaque
/// access token the broker minted and will rotate; it is NOT a stored secret and must not be persisted.
/// For <see cref="AuthScheme.None"/> the handle is <see cref="AuthHandle.Empty"/>.
/// </summary>
public sealed record AuthHandle(
    AuthScheme Scheme,
    string? Token,                              // bearer/apikey value; null for None/Basic
    string? Username,                           // Basic / app-password
    string? Password,                           // Basic / app-password (the app-specific password, never the account password)
    DateTimeOffset? ExpiresAt)                  // when the handle stops being valid; refetch after this
{
    public static readonly AuthHandle Empty = new(AuthScheme.None, null, null, null, null);
}
```

> The broker is *the* reason the app is provider-agnostic: a new OAuth provider needs **no host code**,
> only manifest fields ([PLUGINS.md §6](PLUGINS.md#6-authentication-broker)). Per the Google/Microsoft
> deep-dives, the plugin builds its API client over a credential that calls `GetTokenAsync` for a fresh
> bearer on each request and never instantiates MSAL or holds `ClientSecrets`.

---

## 4. Calendar capabilities

`calendar.read` and `calendar.write` are implemented by `ICalendarSource` / `ICalendarWriter`. These are
the contracts the Google, Microsoft Graph, CalDAV, and ICS deep-dives all map onto — the field set below is
the **union of every normalized field** those deep-dives assume, made canonical here.

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>calendar.read</c>. Read &amp; normalize calendars and events with delta sync.</summary>
public interface ICalendarSource : IPlugin
{
    /// <summary>Enumerate the calendars this account exposes (primary, shared, Holidays, Birthdays, …).</summary>
    Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(CancellationToken ct);

    /// <summary>
    /// Delta sync one calendar. <paramref name="syncToken"/> is null on the first (full) sync; otherwise it
    /// is the opaque token returned by a prior call (Google <c>nextSyncToken</c>, Graph
    /// <c>@odata.deltaLink</c>, CalDAV <c>sync-token</c>, or the ICS plugin's self-minted
    /// <c>etag|lastmod|bodyhash</c>). On an invalidated token (e.g. Google/Graph <c>410</c>) the plugin
    /// throws <see cref="SyncResetRequiredException"/> so the host wipes the token and re-runs a full sync.
    /// </summary>
    Task<SyncResult> SyncAsync(string remoteCalendarId, string? syncToken, CancellationToken ct);
}

/// <summary>capability <c>calendar.write</c> (later phase). Optimistic-concurrency writes via change tags.</summary>
public interface ICalendarWriter : IPlugin
{
    /// <summary>Create an event on a calendar; returns the created event with its assigned RemoteId/ChangeTag.</summary>
    Task<RemoteEvent> CreateEventAsync(string remoteCalendarId, RemoteEvent draft, CancellationToken ct);

    /// <summary>
    /// Update an event. <paramref name="ifMatchChangeTag"/> carries the prior <see cref="RemoteEvent.ChangeTag"/>
    /// (ETag/changeKey) for optimistic concurrency; on a precondition failure (CalDAV/Google/Graph <c>412</c>)
    /// the plugin throws <see cref="ConcurrencyConflictException"/> so the host re-fetches and merges.
    /// </summary>
    Task<RemoteEvent> UpdateEventAsync(string remoteCalendarId, RemoteEvent updated, string? ifMatchChangeTag, CancellationToken ct);

    /// <summary>Delete an event (optionally guarded by its change tag).</summary>
    Task DeleteEventAsync(string remoteCalendarId, string remoteEventId, string? ifMatchChangeTag, CancellationToken ct);
}
```

```csharp
/// <summary>A calendar as the provider exposes it, normalized. Maps to the domain CALENDAR ([ARCHITECTURE.md §9]).</summary>
public sealed record RemoteCalendar(
    string RemoteId,                            // provider id / CalDAV collection href / feed URL — passed back to SyncAsync
    string Name,                                // display name (Google summary, Graph name, CalDAV displayname, ICS X-WR-CALNAME)
    string? Color,                              // hex color if the provider supplies one
    bool IsReadOnly);                           // true for Holidays/Birthdays, reader-role calendars, ICS feeds

/// <summary>
/// A normalized event. The field set is the canonical union the calendar deep-dives map onto; storage keys
/// on <see cref="Uid"/> for dedup and <see cref="RemoteId"/> for addressing.
/// </summary>
public sealed record RemoteEvent(
    string RemoteId,                            // provider-unique id within the calendar (Google id, Graph id, CalDAV href, ICS (UID,RECURRENCE-ID))
    string Uid,                                 // stable cross-system id (iCalUID / UID) — FEEDS DEDUP across accounts
    string Title,                               // SUMMARY / summary / subject
    DateTimeOffset StartUtc,                    // resolved to UTC for storage
    DateTimeOffset EndUtc,
    string? TimeZoneId,                         // originating IANA tz id (null for all-day/floating); UI renders in user's zone
    bool AllDay,                                // DTSTART;VALUE=DATE / start.date / isAllDay (note: all-day DTEND is exclusive — host adjusts)
    string? Rrule,                              // master RRULE (+RDATE/EXDATE); recurrence is stored, expanded on demand — never materialized
    string? RecurrenceId,                       // set on an override/exception instance; key occurrences by (Uid, RecurrenceId)
    string? Location,                           // free-form LOCATION text → geocoded to a Place by the host
    GeoPoint? Geo,                              // provider-supplied lat/lng (iCal GEO, Graph location.coordinates) — skips geocoding
    IReadOnlyList<string> Categories,           // CATEGORIES / categories[] → domain category rules ([ARCHITECTURE.md §13])
    string? ChangeTag,                          // per-event ETag / @odata.etag / changeKey — optimistic concurrency for writes
    EventStatus Status);                        // Confirmed/Tentative → upsert; Cancelled → delete (tombstone)

/// <summary>Event lifecycle status. <see cref="Cancelled"/> is a tombstone → the host removes the event.</summary>
public enum EventStatus { Confirmed, Tentative, Cancelled }
```

```csharp
/// <summary>
/// The result of one delta sync round. <see cref="Upserts"/> are normalized events to add/update;
/// <see cref="Deletes"/> are RemoteIds to remove (from tombstones / vanished resources);
/// <see cref="NewSyncToken"/> is the opaque token to replay next time. Upserts are idempotent (Uid-keyed).
/// </summary>
public sealed record SyncResult(
    IReadOnlyList<RemoteEvent> Upserts,
    IReadOnlyList<string> Deletes,
    string NewSyncToken);

/// <summary>
/// Thrown by <see cref="ICalendarSource.SyncAsync"/> when the supplied token is no longer valid (Google/
/// Graph <c>410</c>, CalDAV CTag divergence). The host discards the token, wipes the calendar cache, and
/// re-runs a full sync (token = null).
/// </summary>
public sealed class SyncResetRequiredException : Exception
{
    public SyncResetRequiredException(string? message = null, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Thrown on an optimistic-concurrency precondition failure (HTTP 412) during a write.</summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string? message = null, Exception? inner = null) : base(message, inner) { }
}
```

> **`NewSyncToken`** (and the `syncToken` parameter) is the opaque per-calendar delta token, matching the
> name used throughout the deep-dives and PLUGINS.md §5. It is always provider-opaque — Google's
> `nextSyncToken`, Graph's `@odata.deltaLink`, CalDAV's `sync-token`, or the ICS plugin's minted
> `etag|lastmod|bodyhash`.

---

## 5. Itinerary & pricing

Itinerary import and offer pricing are unified by the **Trip → TripItem** model and the
**`*.price`** aggregator. Per [ARCHITECTURE.md §14](ARCHITECTURE.md#14-travel-itineraries-stays--fares) and
the travel-fares deep-dive, booked itinerary items project into `Event`s; candidate offers paint the
multi-month overlays. Every pricing record carries the **`Source` tag** and **`DeepLink`** the aggregator
needs to dedupe, pick cheapest, and attribute.

### 5.1 Itinerary import (`itinerary.import`)

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>itinerary.import</c>. Import booked trips as Trip + TripItem; items project into Events.</summary>
public interface IItinerarySource : IPlugin
{
    /// <summary>Import trips overlapping the window. Delta semantics mirror calendars: null token = full import.</summary>
    Task<ItineraryResult> ImportAsync(DateOnly from, DateOnly to, string? syncToken, CancellationToken ct);
}

/// <summary>Kinds of trip item ([ARCHITECTURE.md §9] TRIP_ITEM.Kind).</summary>
public enum TripItemKind { Flight, Stay, Car, Rail, Activity }

/// <summary>Whether a trip item is confirmed or a planner draft ([ARCHITECTURE.md §9] TRIP_ITEM.Status).</summary>
public enum TripItemStatus { Booked, Candidate }

/// <summary>A trip groups items over a date span. Maps to the domain TRIP.</summary>
public sealed record Trip(
    string RemoteId,
    string Name,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<TripItem> Items);

/// <summary>One item within a trip. Booked items project into Events (Travel category); candidates feed planner overlays.</summary>
public sealed record TripItem(
    string RemoteId,
    TripItemKind Kind,
    TripItemStatus Status,
    string Title,                               // e.g. "BA117 JFK→LHR", "Hilton Berlin"
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? Location,                           // free-form → geocoded to a Place
    GeoPoint? Geo,                              // explicit coords when known
    string? Confirmation,                       // confirmation/booking reference
    string? DeepLink);                          // link out to the booking/itinerary

/// <summary>Result of one itinerary import round (same delta shape as <see cref="SyncResult"/>).</summary>
public sealed record ItineraryResult(
    IReadOnlyList<Trip> Upserts,
    IReadOnlyList<string> Deletes,              // RemoteIds of removed trips
    string NewSyncToken);
```

> TripIt ships as an **ICS-feed integration** (`itinerary.import` *expressed through* `calendar.read`,
> [ics-plugin.md §12](deep-dives/ics-plugin.md)): its read-only `.ics` items are tagged `Travel` and project
> into events without a dedicated `IItinerarySource` plugin. A true `IItinerarySource` plugin is built only
> if partner API access is obtained — but the interface is canonical so such a plugin slots in unchanged.

### 5.2 Flight & stay pricing (`flight.price` / `stay.price`)

```csharp
/// <summary>capability <c>flight.price</c>. Search interchangeable fare offers (aggregated, deduped, cheapest-picked).</summary>
public interface IFlightPricing : IPlugin
{
    /// <summary>Markets/currencies/route-shapes this provider serves — the aggregator's routing policy reads it.</summary>
    FareCoverage Coverage { get; }

    Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct);
}

/// <summary>capability <c>stay.price</c>. Search interchangeable nightly/total stay offers.</summary>
public interface IStayPricing : IPlugin
{
    FareCoverage Coverage { get; }

    Task<IReadOnlyList<StayOffer>> SearchAsync(StayQuery q, CancellationToken ct);
}

public enum CabinClass { Economy, PremiumEconomy, Business, First }

public sealed record FareQuery(
    string From, string To,                     // IATA codes (city/place → IATA resolved upstream)
    DateOnly DepartDate, DateOnly? ReturnDate,
    int Adults, CabinClass Cabin, string Currency, string Market);

public sealed record StayQuery(
    double Lat, double Lng, int RadiusKm,       // geographic search (or an accommodation id upstream)
    DateOnly CheckIn, DateOnly CheckOut,
    int Adults, int Rooms, string Currency, string Market);

public sealed record FareOffer(
    decimal Price, string Currency,
    string From, string To, DateOnly Date,
    string Carrier, string FlightNo,            // dedupe key (with times); first marketing carrier for multi-segment
    DateTimeOffset DepartUtc, DateTimeOffset ArriveUtc,
    string DeepLink, string Source,             // Source = winning provider id: "duffel" | "kiwi" | …
    DateTimeOffset RetrievedAt, bool Stale);    // timestamped; Stale=true when served from history fallback

public sealed record StayOffer(
    decimal PricePerNight, decimal PriceTotal, string Currency,
    string PlaceLabel, double Lat, double Lng,
    DateOnly CheckIn, DateOnly CheckOut,
    string DeepLink, string Source,
    DateTimeOffset RetrievedAt, bool Stale);

/// <summary>A pricing provider's coverage descriptor, read by the aggregator before fanning a query out.</summary>
public sealed record FareCoverage(
    IReadOnlySet<string> Markets,
    IReadOnlySet<string> Currencies,
    bool MultiCity, bool OneWay, string Notes);
```

> Both pricing capabilities are **interchangeable-result** capabilities, so they ride the
> [fallback aggregator](ARCHITECTURE.md#15-multi-provider-fallback-aggregator): fan-out → normalize →
> dedupe (`Carrier + FlightNo + DepartUtc + ArriveUtc` for flights; accommodation/geo for stays) → keep
> cheapest → tag `Source`. When every provider is down/empty/rate-limited, the aggregator returns last-known
> history with `Stale = true` and the original `RetrievedAt` ([travel-fares-plugin.md §9](deep-dives/travel-fares-plugin.md)).

---

## 6. Geo capabilities

Four pluggable geo capabilities. `geo.geocode`, `geo.route`, and `geo.places` produce interchangeable
results (aggregated); `geo.tiles` is a single chosen basemap (not aggregated). `GeoPoint` and `Place` are
the shared value types.

### 6.1 Shared geo value types

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>A bare coordinate. The canonical lightweight geo value type used across routing, geocoding, and events.</summary>
public readonly record struct GeoPoint(double Lat, double Lng);

/// <summary>An axis-aligned bounding box (viewport) for biasing geocoding/place search. Order matches GeoJSON-ish (min/max lng/lat).</summary>
public readonly record struct BBox(double MinLng, double MinLat, double MaxLng, double MaxLat);

/// <summary>Map-center / viewport bias + language for geocoding and autocomplete ([geo-geocoding-places-plugin.md §2]).</summary>
public sealed record GeoBias(
    double? Lat, double? Lng,                   // map-center proximity bias
    BBox? ViewBox,                              // viewport restriction (Nominatim viewbox, Photon bbox)
    string? Lang);                              // preferred result language (accept-language / lang)

/// <summary>
/// A resolved place. Maps to the domain PLACE ([ARCHITECTURE.md §9]). <see cref="Source"/> records which
/// provider answered (attribution / aggregator tagging).
/// </summary>
public sealed record Place(
    double Lat, double Lng,
    string Label,                               // human-readable display name
    string? Address,                            // parsed/full address when available
    string Source);                             // provider id: "nominatim" | "photon" | "google" | …

/// <summary>One ranked autocomplete candidate from <c>geo.places</c>.</summary>
public sealed record PlaceSuggestion(
    string Label,
    double Lat, double Lng,
    string? Address,
    string Source);
```

### 6.2 Geocoding (`geo.geocode`) and place search (`geo.places`)

```csharp
/// <summary>capability <c>geo.geocode</c>. Text → best place (forward) and coords → place (reverse, for map click-to-place).</summary>
public interface IGeocoder : IPlugin
{
    /// <summary>Forward geocode: one query → the single best <see cref="Place"/>, or null (caller keeps raw text, retries later).</summary>
    Task<Place?> GeocodeAsync(string query, GeoBias? bias, CancellationToken ct);

    /// <summary>Reverse geocode a coordinate to a place (powers map click-to-place). Null if nothing resolves.</summary>
    Task<Place?> ReverseGeocodeAsync(GeoPoint point, CancellationToken ct);
}

/// <summary>capability <c>geo.places</c>. Search-as-you-type autocomplete: partial query + bias → ranked candidates.</summary>
public interface IPlaceSearch : IPlugin
{
    Task<IReadOnlyList<PlaceSuggestion>> SuggestAsync(string query, GeoBias? bias, CancellationToken ct);
}
```

> Both default providers (Nominatim for geocode, Photon for places) are **declarative connectors** — these
> interfaces are satisfied by the connector engine, not hand-written DLLs ([geo-geocoding-places-plugin.md §2](deep-dives/geo-geocoding-places-plugin.md)).
> A `null` forward-geocode result is normal: the host preserves the raw `Location` text and retries on the
> next sync. Results land in `Place` + `GeocodeCache`, keyed on a normalized query.

### 6.3 Routing (`geo.route`)

```csharp
/// <summary>Travel mode the planner asks for ([geo-routing-plugin.md §2]).</summary>
public enum TravelMode { Drive, Transit, Walk, Bike }

/// <summary>capability <c>geo.route</c>. Origin/destination/mode/time → duration + geometry (interchangeable; aggregated).</summary>
public interface IRouteProvider : IPlugin
{
    /// <summary>Modes this provider can actually answer — drives policy (who gets Transit; who is traffic-aware).</summary>
    RouteCoverage Coverage { get; }

    /// <summary>
    /// Route between two points. <paramref name="when"/> is the NEXT EVENT'S START; the plugin interprets it
    /// per mode (Drive → departureTime; Transit → arrivalTime). The plugin returns DurationSec (+ optional
    /// Geometry) only; it MUST leave <see cref="RouteResult.LeaveByUtc"/> null and <see cref="RouteResult.Feasible"/>
    /// true — those are host-computed against the inter-event gap (§ below). Throw
    /// <see cref="System.NotSupportedException"/> for a mode the provider can't serve so the aggregator fails over.
    /// </summary>
    Task<RouteResult> RouteAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct);
}

/// <summary>Which travel modes (and traffic-awareness) a routing provider supports. Read by the aggregator's policy.</summary>
public sealed record RouteCoverage(
    bool Drive, bool Transit, bool Walk, bool Bike,
    bool TrafficAware);                         // true → preferred for time-sensitive drive timing

/// <summary>
/// A routing answer. <see cref="DurationSec"/> and <see cref="Geometry"/> come from the provider;
/// <see cref="LeaveByUtc"/> and <see cref="Feasible"/> are computed by the HOST (they depend on the gap a
/// provider can't see) and left at their defaults by plugins; <see cref="Source"/> is stamped by the aggregator.
/// </summary>
public sealed record RouteResult(
    int DurationSec,                            // provider's predicted travel time (traffic-aware where supported)
    string? Geometry,                           // encoded polyline, precision 5 (normalize Valhalla's precision-6 in the plugin); null if not requested
    DateTimeOffset? LeaveByUtc,                 // HOST-COMPUTED: arriveBy − DurationSec − buffer; plugins leave null
    bool Feasible,                              // HOST-COMPUTED: gap ≥ DurationSec; plugins default true
    string Source);                             // winning plugin id (e.g. "osrm","google") → RouteLeg.Source
```

> The host turns a `RouteResult` into the `POST /route` response and a persisted `RouteLeg`
> ([ARCHITECTURE.md §9](ARCHITECTURE.md#9-domain-model)): `LeaveByUtc = arriveBy − DurationSec − buffer`,
> `Feasible = gap ≥ DurationSec` where `gap = nextEvent.Start − prevEvent.End`. Keeping `LeaveByUtc`/
> `Feasible` host-side is what keeps providers interchangeable ([geo-routing-plugin.md §4](deep-dives/geo-routing-plugin.md)).

### 6.4 Tiles (`geo.tiles`)

```csharp
/// <summary>
/// capability <c>geo.tiles</c>. Supplies the Map view's basemap as a MapLibre style. Usually pure declarative
/// config; the assembly interface exists for providers that must compute a style or mint per-session tokens
/// (e.g. Google) ([geo-tiles-plugin.md §2]).
/// </summary>
public interface ITileProvider : IPlugin
{
    /// <summary>Resolve the basemap to render (called when the Map view mounts and on style switch).</summary>
    Task<StyleDescriptor> GetStyleAsync(CancellationToken ct);
}

/// <summary>A resolved MapLibre basemap. <see cref="StyleUrl"/> and <see cref="StyleJson"/> are mutually exclusive.</summary>
public sealed record StyleDescriptor(
    string? StyleUrl,                           // e.g. https://…/style.json?key=…  (most providers)
    string? StyleJson,                          // inline MapLibre style (self-hosted / PMTiles assembled at runtime)
    string Attribution,                         // required credit line shown in the attribution control — non-optional
    string TileKind,                            // "vector" | "raster" — informational; the style is authoritative
    bool SupportsOffline);                      // may the PWA cache visited tiles? (license-dependent)
```

> `geo.tiles` is **not** an aggregated capability — a basemap is a single chosen surface. If the style fails
> to load, the host falls back to a bundled default style, not to "the next provider"
> ([geo-tiles-plugin.md §2, §10](deep-dives/geo-tiles-plugin.md)).

---

## 7. Notifications

```csharp
namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>notify</c>. Delivers reminders/alerts (fare-drop, leave-by, event reminders) via email/push/webhook.</summary>
public interface INotifier : IPlugin
{
    /// <summary>Channels this notifier can deliver on — the host routes a message to a capable notifier.</summary>
    NotifyChannels Channels { get; }

    /// <summary>Send one notification. Returns when accepted for delivery (not necessarily delivered).</summary>
    Task NotifyAsync(Notification message, CancellationToken ct);
}

/// <summary>Delivery channels a notifier supports.</summary>
public sealed record NotifyChannels(bool Email, bool Push, bool Webhook);

/// <summary>Severity / intent hint for rendering and routing.</summary>
public enum NotifyKind { Reminder, FareDrop, LeaveBy, Conflict, SyncError, Info }

/// <summary>A notification to deliver. <see cref="DeepLink"/> opens the relevant calendar/trip/fare surface.</summary>
public sealed record Notification(
    NotifyKind Kind,
    string Title,
    string Body,
    string? DeepLink,
    DateTimeOffset CreatedAt);
```

> `notify` is fired by the scheduler — e.g. a `FareWatch` whose new low drops below the prior low by a
> threshold ([travel-fares-plugin.md §10](deep-dives/travel-fares-plugin.md)), or a planner "leave by"
> alert ([ARCHITECTURE.md §7](ARCHITECTURE.md#7-map-geocoding--routing)).

---

## 8. Declarative connectors satisfy the same interfaces

A **declarative connector** is data, not code: a manifest + an OpenAPI document + JSONata/JMESPath mapping
expressions. It implements **the same capability interfaces** as an assembly plugin — but the implementation
is the host's shared **connector engine**, which the host surfaces to the registry as an `IPlugin` of the
declared capabilities ([PLUGINS.md §4](PLUGINS.md#4-declarative-connectors-read-apis--load-them)). The core
cannot tell the two kinds apart; both answer `capability → [plugins]` lookups identically.

How a manifest binds to the contract:

| Manifest element | Maps to |
| --- | --- |
| `kind: declarative` | `PluginManifest.Kind == PluginKind.Declarative` |
| `capabilities: [...]` | the capability interface(s) the connector engine presents (e.g. `[geo.geocode]` → `IGeocoder`) |
| `operations.<name>.call` | the OpenAPI operation invoked for the corresponding capability method |
| `operations.<name>.query`/`body`/`headers` | bind the method's argument record (`FareQuery`, `GeoBias`, …) to request params |
| `operations.<name>.map` (JSONata) | shape the response JSON into the capability's return DTO(s) (`FareOffer[]`, `Place?`, `RouteResult`, …) |
| `auth` | the broker scheme applied to every request (the plugin still never sees the secret) |
| `network.allow` | the egress allowlist enforced on the engine's `HttpClient` |

**Example — Nominatim implements `IGeocoder.GeocodeAsync` with no code**
([geo-geocoding-places-plugin.md §4.3](deep-dives/geo-geocoding-places-plugin.md)). The `geocode` operation
binds `GeocodeAsync(query, bias)` to `GET /search` and the JSONata `map` produces a `Place?`:

```yaml
operations:
  geocode:                                  # implements geo.geocode → IGeocoder.GeocodeAsync
    call: GET /search
    query:
      q: "{q.query}"
      format: "jsonv2"
      addressdetails: "1"
      limit: "1"
      viewbox: "{q.bias.viewbox}"           # omitted when null
      "accept-language": "{q.bias.lang}"
    map: |                                   # JSONata: response[] → Place?  ($[0] = best hit; empty array → null)
      $[0].{
        "Lat":     $number(lat),
        "Lng":     $number(lon),
        "Label":   display_name,
        "Address": display_name,
        "Source":  "nominatim"
      }
```

The engine validates the response against the OpenAPI schema, applies auth/pagination/retry/egress-filtering,
and returns a `Place?` exactly as a hand-written `IGeocoder` would. Reach for an **assembly** plugin only when
the protocol isn't "call endpoints, map JSON" — CalDAV's WebDAV/XML, Graph's stateful delta, or ICS's
iCalendar parsing ([PLUGINS.md §4](PLUGINS.md#4-declarative-connectors-read-apis--load-them)).

---

## 9. Capability → interface index

| Capability id | Enum | Interface | Key DTOs | Aggregated? |
| --- | --- | --- | --- | --- |
| `calendar.read` | `Capability.CalendarRead` | `ICalendarSource` | `RemoteCalendar`, `RemoteEvent`, `SyncResult` | no |
| `calendar.write` | `Capability.CalendarWrite` | `ICalendarWriter` | `RemoteEvent`, `ChangeTag` | no |
| `itinerary.import` | `Capability.ItineraryImport` | `IItinerarySource` | `Trip`, `TripItem`, `ItineraryResult` | no |
| `flight.price` | `Capability.FlightPrice` | `IFlightPricing` | `FareQuery`, `FareOffer`, `FareCoverage` | **yes** |
| `stay.price` | `Capability.StayPrice` | `IStayPricing` | `StayQuery`, `StayOffer`, `FareCoverage` | **yes** |
| `geo.geocode` | `Capability.GeoGeocode` | `IGeocoder` | `Place`, `GeoPoint`, `GeoBias` | **yes** |
| `geo.places` | `Capability.GeoPlaces` | `IPlaceSearch` | `PlaceSuggestion`, `GeoBias` | **yes** |
| `geo.route` | `Capability.GeoRoute` | `IRouteProvider` | `RouteResult`, `RouteCoverage`, `TravelMode` | **yes** |
| `geo.tiles` | `Capability.GeoTiles` | `ITileProvider` | `StyleDescriptor` | no (single chosen basemap) |
| `notify` | `Capability.Notify` | `INotifier` | `Notification`, `NotifyChannels` | no |

All capability interfaces derive from `IPlugin`; a single plugin may implement several (e.g. a provider that
serves both `geo.geocode` and `geo.places`, declaring both in `manifest.capabilities`).

---

## Reconciliation notes

These are the small naming/shape drifts across the deep-dives that this contract **normalized**. Each is
called out so the canonical choice can be verified against the source docs.

1. **`GeoPoint` had two definitions.** [geo-routing-plugin.md §2](deep-dives/geo-routing-plugin.md) defined
   `readonly record struct GeoPoint(double Lat, double Lng)`; [geo-geocoding-places-plugin.md §2](deep-dives/geo-geocoding-places-plugin.md)
   defined `record GeoPoint(double Lat, double Lng, string Label, string? Address, string Source)`.
   **Canonical:** `GeoPoint` is the **bare coordinate** `readonly record struct (Lat, Lng)` (the routing
   shape). The richer "resolved location" type is **`Place(Lat, Lng, Label, Address?, Source)`** — which is
   what geocoding actually returns. The geocoding deep-dive's "`GeoPoint` with label/address/source" is
   merged into `Place`.

2. **`GeocodeAsync` / `ReverseGeocodeAsync` return type.** [PLUGINS.md §5](PLUGINS.md#5-assembly-plugins--the-sdk-contract)
   had `GeocodeAsync → GeoPoint?` and `ReverseGeocodeAsync(GeoPoint) → Place?`; the geocoding deep-dive had
   `GeocodeAsync → GeoPoint?` (rich) and `ReverseGeocodeAsync(double lat, double lng) → GeoPoint?`.
   **Canonical:** both return **`Place?`** (forward geocode resolves to a labeled place; null = no result),
   and **`ReverseGeocodeAsync` takes a `GeoPoint`** (not loose `double lat, double lng`) for consistency
   with the rest of the surface.

3. **Routing coverage type name.** [PLUGINS.md §5](PLUGINS.md#5-assembly-plugins--the-sdk-contract) named no
   coverage type on `IRouteProvider`; [geo-routing-plugin.md §2](deep-dives/geo-routing-plugin.md) used
   `RouteCoverage`. **Canonical:** **`RouteCoverage`** (booleans per mode + `TrafficAware`). Distinct from
   `FareCoverage` (pricing), which is unchanged.

4. **Sync token naming.** Every calendar deep-dive declared
   `SyncResult { …; string NewSyncToken; }` and a `SyncAsync(…, string? syncToken, …)` parameter.
   **Canonical:** kept as **`NewSyncToken`** / **`syncToken`** — an opaque `string`, not a wrapper struct —
   matching the deep-dives, PLUGINS.md §5, and how all four calendar plugins actually mint and replay it
   (Google `nextSyncToken`, Graph `@odata.deltaLink`, CalDAV `sync-token`, ICS `etag|lastmod|bodyhash`).

5. **`SyncResult.Upserts` element type.** The CalDAV/Google/Graph/ICS deep-dives wrote
   `IReadOnlyList<RemoteEvent> Upserts`. **Canonical:** kept as `RemoteEvent` (the normalized event), and the
   normalized field set was made the **union** of all fields those deep-dives map onto (adding `TimeZoneId`,
   `Geo`, `RecurrenceId`, `ChangeTag`, `Status`) so no plugin loses a field it relied on. `RemoteEvent` is
   the provider-facing record; the host's persisted `Event` ([ARCHITECTURE.md §9](ARCHITECTURE.md#9-domain-model))
   is derived from it.

6. **Change/concurrency tag.** Deep-dives variously call it `ETag`, `@odata.etag`, `changeKey`, or
   "change token". **Canonical:** the single field **`RemoteEvent.ChangeTag`**, with `412`-conflict writes
   surfacing **`ConcurrencyConflictException`** and cursor-invalidation surfacing
   **`SyncResetRequiredException`** (the deep-dives describe these `412`/`410` flows in prose; the exception
   types are pinned here so the host's conflict/resync policy is uniform across plugins).

7. **Pricing `Coverage` type.** [travel-fares-plugin.md §2](deep-dives/travel-fares-plugin.md) gave both
   `IFlightPricing` and `IStayPricing` a `FareCoverage Coverage`. **Canonical:** kept verbatim — `FareCoverage`
   is shared by both pricing interfaces (stays reuse the markets/currencies fields; `MultiCity`/`OneWay` are
   simply not meaningful for stays and left default).

8. **`ConfigSchema` shape.** Manifests express `config` as inline JSON Schema. **Canonical:** modeled as
   `ConfigSchema(string JsonSchema, IReadOnlyList<string> Required)` carrying the raw schema document (the
   host already needs the literal JSON Schema to drive the [schema-driven UI](PLUGINS.md#9-schema-driven-configuration-ui)),
   rather than a strongly-typed property tree — plugins read their typed view via `IPluginHost.GetConfig<T>()`.
