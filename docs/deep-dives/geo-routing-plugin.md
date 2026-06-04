# Deep dive: the geo-routing plugins (`geo.route`)

`geo.route` is the capability behind the **killer feature**: the calendar knows how long it takes to get
between two consecutive events and turns that into a **commute chip**, a **"leave by HH:MM"** hint, and a
**"you can't make it" conflict warning**. Unlike `calendar.read` (one CalDAV implementation unlocks many
hosts), routing is an **interchangeable** capability: many providers answer the same `(from, to, mode,
when) → duration` question with different coverage, cost, and accuracy. So this capability is built
*around the [fallback aggregator](../ARCHITECTURE.md#15-multi-provider-fallback-aggregator)*, not a single
provider. This document specifies the SDK mapping, each provider and how to add it, the result→API→DB
mapping, the planner integration, the aggregator policy, manifests, and failure modes.

- [1. What `geo.route` is](#1-what-georoute-is)
- [2. Capability & SDK mapping](#2-capability--sdk-mapping)
- [3. Providers & how to add each](#3-providers--how-to-add-each)
  - [3.1 OSRM (self-host, 🟢)](#31-osrm-self-host-)
  - [3.2 Valhalla (self-host, 🟢)](#32-valhalla-self-host-)
  - [3.3 Google Routes API (🟡 paid)](#33-google-routes-api--paid)
  - [3.4 Mapbox Directions (🟡 keyed)](#34-mapbox-directions--keyed)
- [4. RouteResult → API → RouteLeg](#4-routeresult--api--routeleg)
- [5. Planner integration](#5-planner-integration)
- [6. Aggregator & routing policy](#6-aggregator--routing-policy)
- [7. Manifests](#7-manifests)
- [8. Provider quirks](#8-provider-quirks)
- [9. Failure modes & tests](#9-failure-modes--tests)
- [10. Sources](#10-sources)

---

## 1. What `geo.route` is

A `geo.route` provider answers one question: **given an origin, a destination, a travel mode, and a
time, how long does the trip take and what path does it follow?** The four modes the planner uses are
**drive / transit / walk / bike**. Two facts shape the whole design:

- **Time matters per mode.** For drive, `when` is a *departure* time and the good providers return a
  **traffic-aware** duration. For **transit**, `when` is effectively an *arrival* time — the next event's
  start — and the provider must respect published schedules. Walk/bike are time-independent.
- **No single provider covers everything.** Self-hosted **OSRM/Valhalla** give free, private, unlimited
  drive/bike/walk routing but **no public-transit schedules** and **no live traffic**. **Google/Mapbox**
  add transit and traffic-aware driving but are **paid and keyed**. The capability therefore must be
  pluggable and aggregated — the core asks "who can route transit right now?" and the policy decides.

So a routing plugin is: **build a request (profile + coordinates + time) → call the HTTP route API →
decode duration + geometry → normalize to `RouteResult` → (host) derive `leaveByUtc` and `feasible`.**

Most routing providers are clean REST/JSON, so they are **declarative connectors** (manifest + OpenAPI +
mapping) per [PLUGINS.md §4](../PLUGINS.md#4-declarative-connectors-read-apis--load-them). An **assembly
plugin** is only needed when polyline decoding or time-semantics translation is easier in C# than in a
JSONata mapping (Google's request shape is the usual reason — see §3.3).

## 2. Capability & SDK mapping

Routing plugins implement `IRouteProvider` from `Calendar.Plugin.Abstractions`
([PLUGINS.md §5](../PLUGINS.md#5-assembly-plugins--the-sdk-contract)):

```csharp
public interface IRouteProvider : IPlugin
{
    // Modes the provider can actually answer (drives policy: who gets transit queries).
    RouteCoverage Coverage { get; }                 // e.g. { Drive, Bike, Walk } vs { ..., Transit, Traffic }

    Task<RouteResult> RouteAsync(
        GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct);
}

public enum TravelMode { Drive, Transit, Walk, Bike }

public readonly record struct GeoPoint(double Lat, double Lng);

public sealed record RouteResult(
    int      DurationSec,      // provider's predicted travel time (traffic-aware where supported)
    string?  Geometry,         // encoded polyline (precision 5) for map drawing; null if not requested
    DateTimeOffset? LeaveByUtc,// set by the host from `when` − DurationSec − buffer (see §4); providers leave null
    bool     Feasible,         // host-computed against the inter-event gap (§4); providers default true
    string   Source);          // winning plugin id, stamped by the aggregator for attribution/RouteLeg.Source
```

Notes on the contract:

- **`when` is mode-typed by the caller.** The planner passes the **next event's start** as `when` and the
  plugin interprets it per mode: drive → `departureTime` ≈ now/scheduled; transit → `arrivalTime`. The
  SDK keeps a single `when` so the interface stays provider-agnostic; the *plugin* maps it to the right
  API parameter.
- **`DurationSec` only** comes from the provider. **`LeaveByUtc` and `Feasible` are host concerns** —
  they depend on the *gap* between events, which a routing provider can't see. Plugins return them as
  `null`/`true`; the planner fills them (§4). This keeps providers interchangeable.
- **`Coverage`** is the hook the aggregator uses to route a `Transit` query only to providers that can
  actually do transit, and to prefer traffic-aware providers for drive timing (§6).

## 3. Providers & how to add each

### 3.1 OSRM (self-host, 🟢)

**Open Source Routing Machine** — the free default for **drive / bike / walk**. Self-hosted ⇒ no quotas,
no key, fully private. **No public-transit schedules and no live traffic** — that's its hard limit.

- **Endpoint:** `GET /route/v1/{profile}/{lon,lat;lon,lat}?overview=full&geometries=polyline`
  - Coordinates are **`lon,lat`** (longitude first — the classic footgun), semicolon-separated.
  - `profile` is **baked into the prepared data** (one dataset per profile), not a query param. Standard
    Lua profiles: **`car` / `bike` / `foot`** → map `TravelMode.Drive/Bike/Walk`. `Transit` ⇒ **not
    supported**, throw `NotSupportedException` so the aggregator fails over (§6).
  - `geometries=polyline` returns a Google-format **precision-5** encoded polyline → straight into
    `RouteResult.Geometry`. `overview=full` for map drawing; `overview=false` when only duration is
    needed (commute chip), to shrink the response.
- **Response:** `{ code:"Ok", routes:[{ duration: 1920.4, distance: …, geometry:"…" }], … }`. Map
  `routes[0].duration` (seconds, float → round) → `DurationSec`, `routes[0].geometry` → `Geometry`.
  `code != "Ok"` (e.g. `NoRoute`) ⇒ no-route failure (§9).
- **Time:** OSRM is **time-agnostic** — it ignores `when` and has no traffic model. Use it for the
  free-flow estimate; route traffic-sensitive drive queries to Google/Mapbox when accuracy matters (§6).

**Self-host notes.** Run `osrm/osrm-backend` (Docker). Data prep is a **three-step MLD pipeline, once per
profile**, over an OSM `.pbf` extract:

```bash
# one dataset per profile — repeat with bicycle.lua / foot.lua for bike/walk
osrm-extract   -p /opt/car.lua   region.osm.pbf
osrm-partition region.osrm
osrm-customize region.osrm
osrm-routed    --algorithm mld   region.osrm        # serves :5000
```

- **RAM/disk scale with area and profile.** A country/metro extract is modest; the **full planet `car`
  profile needs ~120 GiB RAM** during pre-processing and hundreds of GiB of scratch disk (rough v5.26
  numbers). **`foot`/`bike` are heavier than `car`** (more ways/paths). Ship **per-region extracts** sized
  to the deployment, not the planet, unless the operator opts in.
- Each profile = a separate prepared dataset and ideally a **separate container/port**; the plugin's
  `baseUrl` + `profile` config selects which.

### 3.2 Valhalla (self-host, 🟢)

**Valhalla** — also self-hostable and free, but **richer than OSRM**: dynamic per-request costing,
**time-dependent routing**, **isochrones**, and — uniquely among the self-host options —
**multimodal/transit** when built with GTFS.

- **Endpoint:** `POST /route` with a JSON body (costing + locations + optional time):

```jsonc
{
  "locations": [ {"lat":52.52,"lon":13.40}, {"lat":52.50,"lon":13.45} ],
  "costing": "auto",                      // auto | bicycle | pedestrian | multimodal
  "costing_options": { "auto": { "use_traffic": false } },
  "date_time": { "type": 2, "value": "2026-07-02T09:00" }  // type 2 = ARRIVE-BY (transit!)
}
```

- **Costing → mode:** `auto`→Drive, `bicycle`→Bike, `pedestrian`→Walk, **`multimodal`→Transit**
  (pedestrian + public transit). Map `TravelMode` to `costing` directly.
- **Time-dependent routing — the reason to prefer Valhalla over OSRM for the planner.** `date_time.type`:
  `0` current, **`1` depart-at**, **`2` arrive-by**, `3` invariant. Transit/`multimodal` requires
  `arrive_by` (type 2) keyed to the **next event start** — exactly the planner's need. Set type 1 for
  scheduled-departure drive estimates.
- **Response:** `trip.summary.time` (seconds) → `DurationSec`; `trip.legs[].shape` is an **encoded
  polyline (precision 6)** → decode/re-encode to precision 5 for `Geometry` (note the precision
  difference vs OSRM/Google). `trip.status != 0` ⇒ no-route/error.
- **Isochrones** (`/isochrone`) are a bonus for a future "what's reachable in 30 min" planning overlay —
  not needed for the commute chip, but the same self-hosted instance provides it.

**Self-host notes.** Run the community `docker-valhalla` image: it builds tiles from an OSM `.pbf` via
`valhalla_build_tiles`. **One tile set serves all road costings** (auto/bike/pedestrian) — no per-profile
duplication like OSRM. **Transit is opt-in and extra work**: mount **GTFS feeds** (one subfolder per
agency, e.g. `gtfs_feeds/berlin/`), set `build_transit=true`, and rebuild; without transit tiles,
`multimodal` silently degrades to **pedestrian-only**. Transit accuracy is **only as fresh as the GTFS
feeds you ingest**, so for accurate transit "leave by" most deployments still prefer Google (§3.3) and use
Valhalla multimodal where good local GTFS exists and privacy matters.

### 3.3 Google Routes API (🟡 paid)

Google is the **best source for accurate "leave by"** on **transit** (real schedules) and **traffic-aware
driving** (`duration` reflects live/predicted traffic). It is **paid**, requires a Google Cloud **billing
account + API key**, and is usage-priced — so it's an **optional, user-configured** plugin that the policy
reserves for transit/traffic queries (§6).

- **Use the current Routes API v2, not the legacy Directions API.** The legacy **Directions API moved to
  *Legacy* status on 2025-03-01** — feature-frozen, unavailable to new Cloud projects, on a 12-month-notice
  path to decommission. New work targets **`POST https://routes.googleapis.com/directions/v2:computeRoutes`**.
- **Request** (POST, body — not GET query params like the legacy API):

```jsonc
// header: X-Goog-Api-Key, X-Goog-FieldMask: routes.duration,routes.polyline.encodedPolyline
{
  "origin":      { "location": { "latLng": {"latitude":52.52,"longitude":13.40} } },
  "destination": { "location": { "latLng": {"latitude":52.50,"longitude":13.45} } },
  "travelMode":  "TRANSIT",                       // DRIVE | TRANSIT | WALK | BICYCLE
  "arrivalTime": "2026-07-02T09:00:00Z",          // TRANSIT → arrivalTime; DRIVE → departureTime
  "routingPreference": "TRAFFIC_AWARE"            // DRIVE only; omit for transit/walk/bike
}
```

  - **Mode map:** `DRIVE / TRANSIT / WALK / BICYCLE`. **Transit** is the headline — pass **`arrivalTime` =
    next event start** for "must arrive by". **Drive** uses **`departureTime`** + `routingPreference:
    TRAFFIC_AWARE` (or `TRAFFIC_AWARE_OPTIMAL`) so `duration` is traffic-aware.
  - A **`FieldMask` is mandatory** — request only `routes.duration` (+ `routes.polyline.encodedPolyline`
    when geometry is needed) to keep the response (and the billable SKU) minimal.
- **Response:** `routes[0].duration` is a string like `"1920s"` → strip `s`, parse → `DurationSec`;
  `routes[0].polyline.encodedPolyline` (precision 5) → `Geometry`. Empty `routes` ⇒ no route.
- **Pricing model (verify at build time).** Pay-as-you-go, billed per request, tiered by feature into
  **Essentials / Pro / Enterprise** SKUs — **`TRAFFIC_AWARE`/`TRAFFIC_AWARE_OPTIMAL` and transit fall into
  the higher (Pro+) tiers**, so they cost more than a plain route. There is a recurring **free monthly
  usage allotment**; beyond it, per-1K rates apply and rise with the SKU tier. Treat exact numbers as
  volatile — **the manifest carries the key; the aggregator carries a rate budget** so a paid provider can
  never run away with cost (§6).
- **Implementation note.** Because the request is a POST with a typed body, a FieldMask header, and
  `"1920s"` duration parsing, Google is the one routing provider usually cleaner as an **assembly plugin**
  (or a declarative connector with a small custom mapping) rather than pure JSONata.

### 3.4 Mapbox Directions (🟡 keyed)

A keyed alternative to Google, simpler pricing, strong **traffic-aware driving** via the
**`driving-traffic`** profile. **No general public-transit routing** (don't route `Transit` here).

- **Endpoint:** `GET /directions/v5/mapbox/{profile}/{lon,lat;lon,lat}?geometries=polyline&overview=full&access_token=…`
  - **Profiles:** `driving-traffic` (traffic-aware) / `driving` / `cycling` / `walking` →
    Drive(+traffic) / Drive / Bike / Walk. `Transit` ⇒ unsupported → fail over.
  - `driving-traffic` **falls back to plain `driving`** where Mapbox has no traffic coverage (transparent).
- **Response:** `routes[0].duration` (seconds) → `DurationSec`; `routes[0].geometry` (polyline precision 5)
  → `Geometry`. `code:"NoRoute"` ⇒ no-route (§9).
- **Pricing (verify at build time):** usage-based, **Directions free tier ≈ 100K requests/month**, then
  per-1K tiers. Single key (`access_token`), no separate billing-account dance — lower friction than
  Google for traffic-aware drive, but **not a transit source**.

## 4. RouteResult → API → RouteLeg

The host turns a provider's `RouteResult` into both the `POST /route` response
([API.md](../API.md#places-geocoding--routing)) and a persisted `RouteLeg` row
([ARCHITECTURE.md §9](../ARCHITECTURE.md#9-domain-model)).

**`POST /route` contract** (unchanged — providers slot under it):

```jsonc
// request
{ "from": {"lat":52.52,"lng":13.40}, "to": {"lat":52.50,"lng":13.45},
  "mode": "transit", "arriveBy": "2026-07-02T09:00:00Z" }
// response
{ "durationSec": 1920, "leaveByUtc": "2026-07-02T08:28:00Z",
  "mode": "transit", "source": "google", "geometry": "<encoded polyline>",
  "feasible": true }
```

**Field-by-field mapping:**

| API / RouteLeg field | Source | Rule |
| --- | --- | --- |
| `durationSec` / `RouteLeg.DurationSec` | `RouteResult.DurationSec` | provider's predicted time (traffic-aware where the source supports it). |
| `leaveByUtc` / `RouteLeg.LeaveByUtc` | **host-computed** | `leaveByUtc = arriveBy − durationSec − buffer` (per-user buffer, e.g. parking/walk-in). |
| `feasible` | **host-computed** | `feasible = gap ≥ durationSec` where `gap = nextEvent.start − prevEvent.end`. `false` ⇒ UI "you can't make it". |
| `mode` / `RouteLeg.Mode` | request `mode` | echoed; one `RouteLeg` per `(FromEventId, ToEventId, Mode)`. |
| `source` / `RouteLeg.Source` | aggregator | winning plugin id (e.g. `osrm`, `google`) for attribution + cache provenance. |
| `geometry` | `RouteResult.Geometry` | encoded polyline (precision 5) for the map's leg line; normalize Valhalla's precision-6 first. |
| `RouteLeg.FromEventId` / `ToEventId` | planner | the consecutive event pair this leg connects. |

**Computing `leaveByUtc`.** For **transit**, `arriveBy` is the **next event's start**, so
`leaveByUtc = nextStart − durationSec − buffer` is the platform-departure-aware "leave by" — the headline.
For **drive/walk/bike** the same formula holds with `arriveBy` = next event start (depart so you arrive on
time). The optional **buffer** absorbs parking, find-the-room, security, etc.

**Computing `feasible`.** `gap = nextEvent.start − prevEvent.end`. If `gap < durationSec` the trip can't be
made → `feasible:false` drives the **conflict warning**. The **boundary** (`gap == durationSec`, or with
buffer `gap == durationSec + buffer`) is exactly where the warning toggles — a tested edge (§9).

**RouteLeg as cache.** Each computed leg is upserted as a `RouteLeg` row keyed by
`(FromEventId, ToEventId, Mode)`. It is both the precompute cache and the **graceful-degradation
fallback**: `GET /events/{id}/commute` reads the stored leg without re-calling a provider, and when every
provider is down the aggregator returns the **last-known `RouteLeg`** flagged stale (§6).

## 5. Planner integration

```mermaid
sequenceDiagram
    participant PL as Planner service
    participant AGG as Route aggregator
    participant REG as Registry (geo.route)
    participant P as Provider plugin
    participant DB as SQLite (RouteLeg)
    PL->>PL: find consecutive events at DIFFERENT Places
    PL->>DB: RouteLeg for (from,to,mode) still valid?
    DB-->>PL: hit → use it (no network)
    PL->>AGG: miss → RouteAsync(from, to, mode, when = nextStart)
    AGG->>REG: who provides geo.route for this mode?
    REG-->>AGG: candidates (Coverage-filtered)
    AGG->>P: call per policy (failover / fan-out, circuit breaker, budget)
    P-->>AGG: RouteResult { durationSec, geometry, source }
    AGG-->>PL: result (or last-known RouteLeg, stale)
    PL->>PL: leaveBy = nextStart − dur − buffer ; feasible = gap ≥ dur
    PL->>DB: upsert RouteLeg(from,to,mode,dur,leaveBy,source)
    PL-->>PL: render commute chip · leave-by · conflict · optional buffer event
```

- **Trigger.** For each pair of **consecutive visible events at different `Place`s**, the planner needs a
  leg. Same place (or no resolved `Place`) ⇒ no routing.
- **What it renders** ([UI.md §6](../UI.md#6-travel-time--directions)): the **commute chip** (`🚗 32 min`)
  in the gap, the **"leave by 14:05"** hint on the earlier event, a **conflict warning** when
  `gap < duration`, and an optional **auto-inserted travel-buffer event** so the time is visibly blocked.
- **Transit uses arrival time.** For `mode:transit` the planner passes `when = nextEvent.start` so the
  provider returns a schedule-respecting itinerary you must *arrive by* — then `leaveBy` is the real
  platform-departure time.
- **Caching & recompute.** Results persist as `RouteLeg` and are **recomputed only when an endpoint or
  time changes** — if either event's `Place` or `Start`/`End` moves, or the user changes mode/buffer, the
  affected leg is invalidated and re-fetched; otherwise the stored leg is reused (and powers offline view).
  A sync delta that touches an event invalidates just its adjacent legs, not the whole range
  ([UI.md §9](../UI.md#9-performance)).
- **Background precompute.** Quartz precomputes legs for the visible/near window
  ([ARCHITECTURE.md §10](../ARCHITECTURE.md#10-sync-engine)) so chips are instant; on-demand fills the rest.

## 6. Aggregator & routing policy

`geo.route` is an **interchangeable** capability, so every call flows through the generic
[fallback aggregator](../ARCHITECTURE.md#15-multi-provider-fallback-aggregator). Routing-specific policy:

- **Default to self-host for free modes.** Drive/bike/walk go to **OSRM or Valhalla** first — free,
  private, no quota. This is the common case and costs nothing.
- **Route transit/traffic to keyed providers by `Coverage`.** A `Transit` query is dispatched **only to
  providers whose `Coverage` includes transit** (Google, or Valhalla-with-GTFS). A traffic-sensitive
  **drive** query prefers a traffic-aware source (Google `TRAFFIC_AWARE`, Mapbox `driving-traffic`) when
  accuracy matters; otherwise the free self-host estimate is fine.
- **Failover vs fan-out.** Default **failover** (first healthy provider for the mode). **Fan-out** is
  rarely needed for routing (results aren't "offers" to dedupe) but can compare a self-host estimate
  against a traffic-aware one. The winning provider id is stamped into `source` / `RouteLeg.Source`.
- **Per-provider guards (Polly).** **Circuit breaker** (trip a failing/slow provider), **rate budget**
  (cap paid Google/Mapbox calls so cost can't run away), **timeout** (a slow route must not stall the
  grid — fall through). These are the same budgets the plugin sandbox enforces
  ([PLUGINS.md §8](../PLUGINS.md#8-security--sandboxing)).
- **Graceful degradation.** If every eligible provider fails or is over budget, return the **last-known
  `RouteLeg`** for the pair, flagged **stale** — the chip still shows (greyed/"~") rather than vanishing.
- **Observability.** Per query: which providers were tried, hit/miss, latency, and the winning source —
  for debugging coverage and cost.

## 7. Manifests

**OSRM** — self-host, no auth, profile-selected, declarative:

```yaml
id: org.unifiedcalendar.osrm
name: OSRM Routing
version: 1.0.0
sdkVersion: "1.x"
kind: declarative
capabilities: [geo.route]                # Coverage: drive, bike, walk (NO transit)
auth:
  scheme: none                           # self-hosted; nothing for the broker to inject
config:
  type: object
  properties:
    baseUrl: { type: string, title: "OSRM base URL", description: "e.g. http://osrm-car:5000" }
    profile: { type: string, title: "Profile", enum: [car, bike, foot], default: car }
  required: [baseUrl]
network:
  allow: ["osrm-car", "osrm-bike", "osrm-foot"]   # internal hosts only — no public egress
openapi: ./osrm-openapi.yaml
operations:
  route:
    call: GET /route/v1/{profile}/{coords}
    path:  { profile: "{config.profile}" }
    query: { overview: "full", geometries: "polyline" }
    map: |                               # JSONata → RouteResult
      { "durationSec": $round(routes[0].duration), "geometry": routes[0].geometry }
```

**Google Routes API** — keyed, paid, transit + traffic-aware:

```yaml
id: org.unifiedcalendar.google-routes
name: Google Routes API
version: 1.0.0
sdkVersion: "1.x"
kind: declarative                        # or assembly if the body/FieldMask mapping is non-trivial
capabilities: [geo.route]                # Coverage: drive (+traffic), transit, walk, bike
auth:
  scheme: apikey                         # broker injects as X-Goog-Api-Key header
  in: header
  name: X-Goog-Api-Key
config:
  type: object
  properties:
    apiKey: { type: string, title: "API key", secret: true }   # → vault, not stored in manifest
  required: [apiKey]
network:
  allow: ["routes.googleapis.com"]
openapi: ./google-routes-openapi.yaml
operations:
  route:
    call: POST /directions/v2:computeRoutes
    headers: { "X-Goog-FieldMask": "routes.duration,routes.polyline.encodedPolyline" }
    body: |                              # mode → travelMode; transit→arrivalTime, drive→departureTime
      { "origin":{"location":{"latLng":{"latitude":"{q.from.lat}","longitude":"{q.from.lng}"}}},
        "destination":{"location":{"latLng":{"latitude":"{q.to.lat}","longitude":"{q.to.lng}"}}},
        "travelMode":"{q.modeUpper}", "arrivalTime":"{q.when}",
        "routingPreference":"TRAFFIC_AWARE" }
    map: |                               # "1920s" → 1920
      { "durationSec": $number($substringBefore(routes[0].duration,"s")),
        "geometry": routes[0].polyline.encodedPolyline }
```

> The Google key is **secret** — it goes to the encrypted vault via the
> [auth broker](../PLUGINS.md#6-authentication-broker), never into the manifest or the UI. Egress is pinned
> to `routes.googleapis.com`; the host's `HttpClient` literally can't reach anything else.

## 8. Provider quirks

| Provider | Notes |
| --- | --- |
| **OSRM** | Coordinates are **`lon,lat`** (not lat,lon); `profile` is **fixed at data-prep**, one dataset/container per profile; **no transit, no traffic**; `code:"NoRoute"` on failure; polyline **precision 5**. |
| **Valhalla** | **One tile set for all road costings**; **`date_time.type` 1=depart / 2=arrive** (transit needs arrive-by); `multimodal` needs **GTFS tiles** or it silently degrades to pedestrian; geometry is polyline **precision 6** (re-encode to 5). |
| **Google Routes** | **Use v2 `computeRoutes`** (legacy Directions API frozen 2025-03-01); **FieldMask header mandatory**; `duration` is a string `"1920s"`; `TRAFFIC_AWARE`/transit are **higher-priced SKUs**; key + billing account required. |
| **Mapbox** | **`driving-traffic`** profile for traffic (falls back to `driving` where uncovered); **no public transit**; `lon,lat` order; single `access_token`; ~100K/mo free tier. |
| **All** | Different coordinate orders and polyline precisions — **normalize in the plugin**, never leak provider quirks into `RouteResult`. |

## 9. Failure modes & tests

- **No route found.** OSRM `code:"NoRoute"` / Mapbox `NoRoute` / empty Google `routes` / Valhalla
  `status!=0` → surface "no route for this mode" and let the aggregator try the next provider; never crash
  the planner grid.
- **Transit unavailable from self-host → fallback.** `RouteAsync(mode:Transit)` against OSRM (or
  GTFS-less Valhalla) must **fail over** to a transit-capable provider via `Coverage` filtering — assert
  the aggregator never returns a pedestrian-only answer mislabeled as transit.
- **429 / quota / budget.** Google/Mapbox 429 or rate-budget exhaustion trips the **circuit breaker** →
  fall through to self-host (drive/bike/walk) or to the **last-known `RouteLeg`** (transit), flagged stale;
  assert paid calls stop when the budget is spent.
- **Polyline decoding.** Round-trip decode/encode for **OSRM/Google precision 5** and **Valhalla
  precision 6**; assert the map leg geometry matches and precision-6 is normalized to 5.
- **Time-zone / DST around departure.** Compute `leaveByUtc` across a **DST transition** and across event
  time zones; assert the "leave by" wall-clock shown to the user is correct on both sides of the change.
- **Ferry / border / long-distance edges.** Routes crossing **ferries or international borders** (toll/
  ferry segments, no-route gaps) return a duration or a clean no-route — never a silent zero.
- **Feasibility boundary.** Parametrize `gap` around `durationSec` (and `durationSec + buffer`): assert
  `feasible` flips exactly at the boundary and the conflict warning matches.
- **Endpoint/time invalidation.** Moving an event's `Place` or `Start` invalidates **only the adjacent
  legs**; an unrelated edit recomputes nothing (cache-correctness test).
- **Mode coverage matrix.** For each provider, assert `Coverage` matches reality (OSRM/Mapbox: no transit;
  Valhalla: transit only with GTFS; Google: all four) so the policy dispatches correctly.
- Integration tests run against **self-hosted OSRM + Valhalla containers** in CI (no paid keys); Google/
  Mapbox paths are tested against **recorded fixtures**.

## 10. Sources

- OSRM Route service (profiles, `geometries`, `overview`, `lon,lat`): <https://project-osrm.org/docs/v5.5.1/api/> · <https://github.com/Project-OSRM/osrm-backend/blob/master/docs/http.md>
- OSRM Docker + MLD pipeline (`osrm-extract`/`partition`/`customize`/`routed`): <https://github.com/Project-OSRM/osrm-backend>
- OSRM disk/RAM requirements (planet `car` ≈120 GiB, profile impact): <https://github.com/Project-OSRM/osrm-backend/wiki/Disk-and-Memory-Requirements>
- Valhalla API reference (costing auto/pedestrian/bicycle/multimodal, `date_time` types, isochrones): <https://valhalla.github.io/valhalla/api/turn-by-turn/api-reference/> · <https://valhalla.github.io/valhalla/api/isochrone/api-reference/>
- Valhalla self-host + GTFS transit tiles (`build_transit`, `gtfs_feeds/`): <https://github.com/nilsnolde/docker-valhalla> · <https://github.com/valhalla/valhalla/blob/master/docker/README.md>
- Google Routes API `computeRoutes` (transit, arrival/departure time, traffic-aware): <https://developers.google.com/maps/documentation/routes/reference/rest/v2/TopLevel/computeRoutes> · <https://developers.google.com/maps/documentation/routes/transit-route>
- Google Routes API usage & billing (SKU tiers, free allotment): <https://developers.google.com/maps/documentation/routes/usage-and-billing>
- Google Directions API → Routes API migration / legacy status (2025-03-01): <https://developers.google.com/maps/documentation/routes/migrate-routes> · <https://developers.google.com/maps/legacy>
- Mapbox Directions API (`driving-traffic` profile, geometries): <https://docs.mapbox.com/api/navigation/directions/>
- Mapbox pricing (Directions free tier + per-1K tiers, 2026): <https://www.mapbox.com/pricing> · <https://docs.mapbox.com/accounts/guides/pricing/>
