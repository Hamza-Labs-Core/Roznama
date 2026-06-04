# Testing strategy & CI specification

**Status: canonical, design-phase.** This document is the **single, consolidated test plan** for Unified
Calendar (.NET 9 / C#). It harvests the scattered **"Failure modes & tests"** sections of every
[deep dive](deep-dives/) into one prioritized strategy, ties them to the entities/indexes in
[DATA-SCHEMA.md](DATA-SCHEMA.md), the plugin contract in [SDK-CONTRACT.md](SDK-CONTRACT.md), the host/auth
model in [PLUGINS.md](PLUGINS.md), and the phased delivery in [ROADMAP.md](ROADMAP.md).

It is a **strategy + CI spec, not test code** — the C# / YAML snippets are illustrative anchors, not a
project. No test project is created by this document. The goal: when an engineer sits down to write the
`Calendar.Domain.Tests` or `Calendar.Integration.Tests` project named in
[ARCHITECTURE.md §18](ARCHITECTURE.md#18-solution-layout), this file tells them **what to test, at which
layer, against which fixtures, and in which CI job** — and which subset is **Phase 0/1 mandatory** vs later.

- [1. Test pyramid & philosophy](#1-test-pyramid--philosophy)
- [2. Unit tests — the pure domain](#2-unit-tests--the-pure-domain)
- [3. Plugin contract tests](#3-plugin-contract-tests)
- [4. Integration tests without live credentials](#4-integration-tests-without-live-credentials)
- [5. Connector-engine tests](#5-connector-engine-tests)
- [6. Host & security tests](#6-host--security-tests)
- [7. Guard / lint tests](#7-guard--lint-tests)
- [8. UI / E2E tests](#8-ui--e2e-tests)
- [9. Test data & fixtures strategy](#9-test-data--fixtures-strategy)
- [10. CI pipeline](#10-ci-pipeline)
- [11. Prioritization — what to build first](#11-prioritization--what-to-build-first)
- [Sources](#sources)

---

## 1. Test pyramid & philosophy

The architecture's clean layering (`Plugin.Abstractions` and `Domain` have **no I/O**; `Application`
orchestrates; `Infrastructure` hosts plugins; `Api`/`Web` are entry points) maps **directly** onto a test
pyramid. Each tier has a different cost, speed, and failure-isolation profile, and lives in a specific
project.

```
        ╱╲          E2E (Playwright)  ── few, slow, whole-system, real browser + host
       ╱  ╲         tests/Calendar.E2E.Tests
      ╱────╲        Integration (Testcontainers + WireMock.Net) ── real protocols, no live creds
     ╱      ╲       tests/Calendar.Integration.Tests
    ╱────────╲      Contract (shared suite every plugin must pass) ── one suite, many providers
   ╱          ╲     tests/Calendar.Contract.Tests
  ╱────────────╲    Unit (pure domain, golden files) ── thousands, milliseconds, deterministic
 ╱──────────────╲   tests/Calendar.Domain.Tests
```

**Philosophy / rules of the road:**

1. **Push logic down the pyramid.** Every behavior that *can* be a pure-domain unit test (dedup signature,
   recurrence expansion, visibility, leave-by math, feasibility) **must** be — these run in milliseconds
   with no Docker, gate every PR, and are where the bulk of the deep-dives' edge cases live.
2. **Determinism is non-negotiable.** No wall-clock (`DateTimeOffset.UtcNow`), no real network, no random
   GUIDs in assertions. Inject an `IClock` / `TimeProvider` and an id generator. Recurrence/DST/feasibility
   tests **freeze the clock**.
3. **Test the contract once, run it for every provider.** Plugin behavior shared across all
   `ICalendarSource` implementations (idempotent upserts, `410`→reset, tombstone deletes) is a **single
   parameterized suite** (§3), not re-written per plugin.
4. **No live credentials, ever, in CI.** Real protocols are exercised against **containers** (CalDAV, geo)
   and **recorded fixtures / WireMock.Net** (Google, Graph, Duffel, Kiwi) — never a live Google OAuth app
   or a billable key. Every deep-dive already commits to this ("Integration tests run against a
   Nextcloud/Radicale container", "recorded Graph fixtures", "Duffel test mode").
5. **Guards are tests.** Policy invariants (no public Nominatim/OSM host shipped; secrets never leave the
   vault; manifests validate) are **automated lint tests** that fail the build, not wiki conventions (§7).
6. **Golden files for anything with a canonical shape.** Recurrence expansion, the projected event stream,
   normalized DTOs, and polyline geometry are **snapshot-tested** (Verify) so a behavior change is a
   reviewable diff, not a hand-maintained pile of `Assert.Equal`.

**Project → layer map** (extends [ARCHITECTURE.md §18](ARCHITECTURE.md#18-solution-layout)'s `tests/`):

| Test project | Layer under test | Speed | Docker? | CI job |
| --- | --- | --- | --- | --- |
| `Calendar.Domain.Tests` | `Calendar.Domain` (pure) | ms | no | `unit` |
| `Calendar.Application.Tests` | `Calendar.Application` (sync orchestration, aggregator) with fakes | ms | no | `unit` |
| `Calendar.Contract.Tests` | every plugin vs the SDK contract | ms–s | sometimes | `unit` / `integration` |
| `Calendar.Integration.Tests` | `Infrastructure`: host (ALC), connector engine, EF/SQLite, real protocols | s | **yes** | `integration` |
| `Calendar.Guards.Tests` | manifests, schema, migrations, SDK compat | ms | no | `lint-guards` |
| `Calendar.E2E.Tests` | `Web` + `Api` end-to-end (Playwright) | s–min | yes (browsers) | `e2e` |

> The ARCHITECTURE doc names only `Calendar.Domain.Tests` and `Calendar.Integration.Tests`; the other three
> (`Application`, `Contract`, `Guards`, `E2E`) are this plan's refinement. `Contract` and `Guards` may start
> as folders inside the two named projects in Phase 0 and graduate to their own projects when they grow.

---

## 2. Unit tests — the pure domain

Target: `Calendar.Domain` (entities + dedup/category/visibility/planner engines, **no I/O**) and the
orchestration helpers in `Calendar.Application`. These are the **highest-value, fastest** tests and the
permanent home of the "Cross-cutting, every phase" items in [ROADMAP.md](ROADMAP.md#cross-cutting-every-phase):
*"dedup engine, recurrence expansion, visibility pipeline, route-gap logic."*

### 2.1 Dedup signature & grouping (ARCHITECTURE §12, DATA-SCHEMA §4.2)

The signature is `hash(normalizedTitle | startDate | allDay | endDate?)` with lower-casing, emoji/
punctuation stripping, and whitespace collapse; birthdays also key on recurring `Uid`/contact, holidays on
date + fuzzy title.

- **Signature normalization (table-driven):** `"  Christmas Day 🎄 "`, `"christmas day"`, `"Christmas  Day"`
  all collapse to one signature; differing dates do **not**. Emoji/punctuation/case/whitespace each get a
  row.
- **Cross-calendar grouping only:** identical signature in **two different calendars** groups; the *same*
  event seen twice in *one* calendar does not. Assert `DuplicateGroup` membership and that
  `DuplicateGroupId` is written.
- **Canonical selection by `Account.Priority`:** given a group spanning accounts with priorities `[0,2,5]`,
  the priority-0 event is canonical; reordering priority re-picks deterministically (API `PATCH /accounts`).
- **Reversibility / overrides:** `ForceMerge`, `NeverMerge`, `SetCanonical` (`DuplicateOverride`, keyed by
  `Uid` so they survive re-sync) produce the expected visible set; an override always wins over the
  signature heuristic.
- **Birthday/holiday keys:** a birthday keyed on recurring `Uid`/contact collapses across Google Birthdays
  + a CalDAV `CATEGORIES:Birthday` source; a holiday collapses on date + fuzzy title across feeds.

### 2.2 Recurrence expansion — GOLDEN FILES (DATA-SCHEMA §3, every calendar deep-dive)

**The single richest edge-case area**, asserted identically by the Google, Graph, CalDAV, and ICS
deep-dives. Occurrences are **never materialized**; the projection service calls
`CalendarEvent.GetOccurrences(from, to)` (Ical.Net) over stored masters + overrides. **Use snapshot testing
(Verify): each input `Event` row(s) + a `[from,to)` window → an approved golden file of expanded instances**
(start/end UTC, `isRecurringInstance`, `masterId`, all-day flag). A behavior change becomes a reviewable
`.received.txt` vs `.verified.txt` diff.

Golden-file matrix (one approved file per case):

| Case | What it pins |
| --- | --- |
| `RRULE` weekly, 10 yr | a one-row master expands to N instances for a bounded window — never 520 rows (DATA-SCHEMA §3). |
| `EXDATE` removal | a cancelled occurrence is **absent** from the window (whole-series removal folded into the master). |
| `RDATE` addition | an extra date appears. |
| `RECURRENCE-ID` override (moved) | a single occurrence is **spliced in** at its new time; the original slot is gone; keyed `(Uid, RecurrenceId)`. |
| `RECURRENCE-ID` override (edited) | title/location change applies to **one** instance only. |
| Cancelled single instance | Google "cancelled instance" / Graph canceled occurrence → occurrence removal, **not** a series delete. |
| All-day single | `VALUE=DATE`, exclusive `DTEND` → inclusive domain end, **no off-by-one, no UTC midnight shift**. |
| All-day multi-day | spans the correct day count. |
| DST-boundary timed | a recurring event across a spring-forward / fall-back transition lands at the correct **wall-clock** each side. |
| Non-UTC `VTIMEZONE` / floating / unknown `TZID` | all resolve to correct UTC instants; unknown TZID degrades sanely. |
| Window straddling a master far in the past | the master recurring **into** the window is found (drives the `IX_Event_Recurring` partial-index query, DATA-SCHEMA §3/§4.1). |

These golden cases are **provider-agnostic** — they live in `Calendar.Domain.Tests` and are fed by
normalized `Event` rows, so they are written **once** and reused by every plugin's contract test (§3) via
the same `.ics`/normalized fixtures.

### 2.3 Visibility pipeline (ARCHITECTURE §13, DATA-SCHEMA §4.1)

The pure boolean pipeline:

```
visible = calendar.IsVisible
          AND category.All(c => c.IsVisible)
          AND NOT (inDuplicateGroup AND not canonical)
          AND withinSelectedRange
```

- **Each clause flips visibility independently:** parametrize one false clause at a time; assert the event
  hides; assert toggling it back shows.
- **"Hide all Birthdays" = one toggle** regardless of source (Google Birthdays calendar vs CalDAV
  `CATEGORIES` vs Graph category) — the headline §13 guarantee.
- **Non-canonical suppression is view-layer, never a delete:** a suppressed duplicate is still reachable as
  "+N" and reappears when the override changes.
- **Range boundary:** an event exactly on `from`/`to` edges (inclusive/exclusive) behaves per the
  `StartUtc < to AND EndUtc > from` rule.

### 2.4 Travel-time: gap / leave-by / feasibility (ARCHITECTURE §7, geo-routing §4/§9, DATA-SCHEMA RouteLeg)

`LeaveByUtc` and `Feasible` are **host-computed** (plugins return only `DurationSec`/`Geometry`), so this is
pure domain math, fully unit-testable:

```
gap        = nextEvent.StartUtc − prevEvent.EndUtc
LeaveByUtc = arriveBy − DurationSec − buffer
Feasible   = gap ≥ DurationSec
```

- **Feasibility boundary (parametrized):** sweep `gap` around `DurationSec` and `DurationSec + buffer`;
  assert `Feasible` flips **exactly** at the boundary and the conflict warning ("you can't make it") matches.
- **Leave-by across DST:** compute `LeaveByUtc` across a DST transition and across event time zones; assert
  the **wall-clock "leave by HH:MM"** shown to the user is correct on **both** sides of the change.
- **Buffer event:** the optional auto-inserted travel buffer event has the right start/duration.
- **Invalidation correctness (cache):** moving an event's `Place` or `StartUtc` invalidates **only the
  adjacent legs**; an unrelated edit recomputes nothing (`UX_RouteLeg_From_To_Mode`, DATA-SCHEMA §4.4).

### 2.5 Category match rules (ARCHITECTURE §13, DATA-SCHEMA Category.MatchRules)

- Source-derived rules: Google Birthdays/Holidays `Kind`, CalDAV `CATEGORIES`, Graph `categories[]` → the
  right domain `Category`.
- User rules: keyword match, calendar-of-origin match; `AssignedBy=User` survives re-evaluation of source
  rules (DATA-SCHEMA `EventCategory.AssignedBy`).
- Precedence when multiple rules match; multi-category events.

### 2.6 Aggregator policy (ARCHITECTURE §15) — `Calendar.Application.Tests`

The fallback aggregator is pure orchestration over fake `IFlightPricing`/`IGeocoder`/`IRouteProvider`:

- **Failover:** provider A throws / returns empty → B is tried; **first good** wins.
- **Fan-out + merge:** dedupe identical results, pick best/cheapest, **tag the winning `Source`** for
  attribution/deep-link.
- **Dedupe keys:** flights on `Carrier + FlightNo + DepartUtc + ArriveUtc` (multi-segment = ordered tuple);
  geocode on normalized query; route by endpoints+mode.
- **Coverage filtering:** a `RouteAsync(Transit)` against an OSRM fake (no transit) **fails over** to a
  transit-capable provider — never returns a pedestrian answer mislabeled transit (geo-routing §9).
- **Stale-history fallback:** all providers down/empty → last-known returned with `Stale=true` + original
  `RetrievedAt` (travel-fares §9, geo-routing §9).
- **Snapshot the observability record:** which plugins were tried, hit/miss, latency placeholder, winning
  source (Verify-friendly).

**Recommended tooling for §2:** xUnit (test runner), **Verify** (snapshot/golden files for §2.2 recurrence
and §2.6 observability records), `TimeProvider`/fake clock, FluentAssertions (optional), Bogus (optional
synthetic data). All in-process, no Docker — these gate **every** PR.

---

## 3. Plugin contract tests

A **shared, parameterized test suite** that **every** plugin implementing a given capability interface must
pass — written once against `Calendar.Plugin.Abstractions`, run against each provider (first-party and, in
future, third-party). This is how the SDK contract's normalized shapes (`SyncResult`, `RemoteEvent`,
`SyncResetRequiredException`, `ConcurrencyConflictException`) stay honored uniformly.

Structure: an abstract `ICalendarSourceContract` (xUnit base class / `[Theory]` data source) parameterized
over `(plugin instance, fixture set)`. Each plugin supplies a factory + its recorded fixtures; the suite
asserts the **contract**, not provider specifics.

### 3.1 `ICalendarSource` contract (Google, Graph, CalDAV, ICS)

| Contract assertion | Source in the deep-dives |
| --- | --- |
| **Idempotent upserts.** Running `SyncAsync` twice over the same payload yields the same DB state; upserts key on `(CalendarId, RemoteId)` (`UX_Event_Calendar_RemoteId`) and `Uid`. | Graph "idempotent upserts"; ARCHITECTURE §10. |
| **Tombstone deletes.** `Status=Cancelled` / `@removed` / vanished `Uid` / `STATUS:CANCELLED` map to `SyncResult.Deletes`, **never** to `Upserts`. | Graph `@removed`; ICS diff; Google deletions. |
| **`410` → `SyncResetRequiredException`.** An invalidated `syncToken` (Google/Graph `410 Gone`, CalDAV CTag divergence) throws it; the host drops the token, wipes the calendar cache, re-runs full sync, and the **final state equals a clean full sync**. | SDK-CONTRACT §4; Google/Graph/CalDAV failure modes. |
| **Cursor replay / pagination.** `NewSyncToken` is taken only from the **last page**; nothing dropped across pages; replaying the token returns the delta only. | Google "pagination correctness"; Graph paging-loop guard. |
| **Normalization field coverage.** Every field in `RemoteEvent` (the union: `Uid`, `RemoteId`, `Title`, `StartUtc/EndUtc`, `TimeZoneId`, `AllDay`, `Rrule`, `RecurrenceId`, `Location`, `Geo`, `Categories`, `ChangeTag`, `Status`) is populated when the source provides it — asserted via a **golden normalized snapshot** per fixture. | SDK-CONTRACT §4 reconciliation note 5. |
| **Recurrence keying.** Overrides key on `(Uid, RecurrenceId)`; masters carry `Rrule`, overrides do not (feeds §2.2 golden files from real provider payloads). | DATA-SCHEMA §3; ICS §16. |
| **Read-only calendars surfaced.** Holidays/Birthdays / reader-role / ICS feeds appear as `RemoteCalendar(IsReadOnly:true)` and **reach the dedup engine** — never silently filtered. | Google "Holidays/Birthdays surfaced". |
| **No token self-persistence.** The plugin never writes a token itself; it only returns `NewSyncToken` (broker/host owns state). | Google/Graph token-refresh. |

### 3.2 `ICalendarWriter` contract (Phase 6)

- `412` precondition failure on `UpdateEventAsync(ifMatchChangeTag)` throws `ConcurrencyConflictException`;
  host re-fetches and merges.
- Create returns the assigned `RemoteId`/`ChangeTag`; delete is idempotent.

### 3.3 `IFlightPricing` / `IStayPricing` contract (Duffel, Kiwi)

- **Offer expiry:** an offer past `expires_at` is rendered `Stale`/indicative, never live; clicking
  re-shops (travel-fares §13).
- **Empty/partial = degradation, not error:** `offers == []` never throws to the planner.
- **`Coverage` matches reality:** `FareCoverage.Markets/Currencies/MultiCity/OneWay` is accurate so the
  aggregator's routing policy dispatches correctly.
- **Field coverage:** `FareOffer`/`StayOffer` carry `Source`, `DeepLink`, `RetrievedAt`, `Stale`.

### 3.4 `IGeocoder` / `IPlaceSearch` / `IRouteProvider` contract

- **`GeocodeAsync` null is normal:** garbage query → `null`, raw text preserved, no empty `Place`
  (geo-geocoding §11).
- **GeoJSON coordinate order:** a deliberately swapped `[lng,lat]` fixture must **fail** (the single most
  common geo bug).
- **`RouteResult` discipline:** the plugin returns `DurationSec`/`Geometry` only; `LeaveByUtc` stays null
  and `Feasible` stays true (host computes them, §2.4); `NotSupportedException` for an unserved mode so the
  aggregator fails over.
- **Polyline round-trip:** decode/encode OSRM/Google precision-5 and Valhalla precision-6, normalized to 5
  (geo-routing §9).

> Contract tests for capabilities backed by **declarative connectors** (Nominatim, Photon, OSRM, Duffel)
> run the **same suite** against the connector engine output — proving §8 of SDK-CONTRACT ("declarative
> connectors satisfy the same interfaces") is actually true.

---

## 4. Integration tests without live credentials

Target: `Calendar.Integration.Tests` — the `Infrastructure` layer against **real protocols**, using
**Testcontainers for .NET** (real servers in Docker) and **WireMock.Net** (recorded HTTP). **Zero live
credentials, zero billable keys, zero real bookings** — exactly what every deep-dive commits to.

### 4.1 Container matrix

| Capability | Container(s) | Asserts (from deep-dives) |
| --- | --- | --- |
| `calendar.read` CalDAV | **Radicale** (light, fast) + **Nextcloud** (realistic quirks) | `PROPFIND` discovery; `calendar-query REPORT`; **`sync-collection` vs CTag/ETag fallback produce identical upserts/deletes**; Multi-Status 200/404 partials parsed without dropping good data; ETag-diff avoids re-downloading unchanged resources; 401 → "use app-specific password" guidance (caldav §13). |
| `geo.geocode` | **Nominatim** (country extract) | forward/reverse; ambiguous-query importance ordering; non-Latin scripts round-trip; viewbox/proximity bias actually reorders; **cache hit = zero outbound calls** (`UX_GeocodeCache_QueryHash`) (geo-geocoding §11). |
| `geo.places` | **Photon** (country extract) | autocomplete ranking; `[lng,lat]`→`Lat/Lng`; session-token correctness for commercial path via WireMock (geo-geocoding §11). |
| `geo.route` | **OSRM** + **Valhalla** | no-route handled cleanly; transit-unavailable → coverage failover; polyline precision; ferry/border edges return duration or clean no-route, never silent zero (geo-routing §9). |
| `geo.tiles` | (no server) static-style fixtures + WireMock | style 404/5xx → bundled-default fallback; PMTiles Range `206`; tile 429 toast; attribution always rendered (geo-tiles §10) — mostly E2E (§8). |
| Storage | EF Core + **SQLite** (file or in-memory-on-disk; **SQLCipher** keyed) | migration round-trip (§7); upsert idempotency on the unique indexes; recurrence query uses `IX_Event_Calendar_Time` + `IX_Event_Recurring`. |

### 4.2 WireMock.Net / recorded-fixture matrix (no container server exists or creds are gated)

| Provider | Approach | Asserts |
| --- | --- | --- |
| **Google Calendar** | Recorded cassettes / mocked `CalendarService` (or WireMock OpenAPI stub) | token refresh mid-sync (broker refreshes, plugin retries, never self-persists); **`410` resync** == clean sync; `403`/`429 rateLimitExceeded` → Polly backoff+jitter honoring `quotaUser`; pagination `nextSyncToken` from last page only; all-day/DST/recurrence golden parity (google §15). |
| **Microsoft Graph** | Recorded delta-page fixtures (`@removed`, recurrence, exceptions) + MSAL test harness | `deltaLink` `410`/`syncStateNotFound` → full resync idempotent; `429 + Retry-After:5` waited exactly, no busy-loop; **paging-loop guard** halts a non-terminating `@odata.nextLink`; `AADSTS65001`/`AADSTS90094` → clear admin-consent message; consumer vs Exchange-Online parity (graph §14). |
| **Duffel** (`flight.price`/`stay.price`) | **Duffel test mode** deterministic fixture routes | offer expiry; empty/partial = degradation; `429 + ratelimit-reset` honored, breaker opens, fan-out completes from other providers; dedupe correctness; stale-history fallback; missing `Duffel-Version: v2` → clear config error; rate-budget (60/60s) (travel-fares §13). |
| **Kiwi (Tequila)** | Recorded Tequila fixtures | second-provider fan-out; missing key → "not configured" (no provider registered). |
| **ICS feeds** | **Static-file fixture server** (canned `.ics` + tunable `ETag`/`Last-Modified`/`Cache-Control`) | malformed ICS degrades gracefully (keep last good snapshot, don't wipe); `304` / byte-identical `200` → empty `SyncResult`, no re-parse; huge feed streams within `maxBytes` (gzip, bounded memory); feed-URL rotation `401/403/404` → backoff stops, "re-enter feed URL", **events retained**; `webcal://`→`https` rewrite re-checks allowlist; validator-less feed body-hash short-circuit; captured Proton Full/Limited + TripIt fixtures normalize correctly (ics §16). |
| **mock OAuth IdP** | A stub identity provider (WireMock or a tiny test IdP) for the **broker** | PKCE auth-code dance; silent refresh; revoked refresh-token → "reconnect"; client-credentials; scoped short-lived handle never leaks the refresh token to the plugin (§6). |

**Recommended tooling for §4:** Testcontainers for .NET; **WireMock.Net** (request matching + response
templating; OpenAPI-driven stubbing; optionally `WireMock.Net.Testcontainers` to run it as a container);
xUnit collection fixtures to share a container across a test class; `Respawn` to reset SQLite between tests.

---

## 5. Connector-engine tests

The connector engine (`Infrastructure`) turns a manifest + OpenAPI + JSONata mapping into an `IPlugin`
(SDK-CONTRACT §8). It is the **shared substrate** for Nominatim, Photon, OSRM, Duffel, and any future
declarative plugin, so its correctness is **leverage** — test it hard, in isolation, with a WireMock backend.

Feed the engine a **sample OpenAPI + mapping** and assert:

- **DTO output:** a `GET /search` response → the exact `Place?` the SDK-CONTRACT §8 Nominatim example
  produces (`$[0]` best hit, empty array → `null`); a `FareOffer[]` from the PLUGINS §4 example mapping.
- **Auth application:** the declared `auth` scheme is applied to every request (apikey header/query per
  `In`/`Name`/`Format`; bearer from broker) and the plugin **never** sees the raw secret.
- **Pagination:** declared pagination is followed; a multi-page response is fully consumed.
- **Error mapping:** `429`/`5xx` → Polly retry/backoff/circuit-breaker; provider error JSON → the right
  capability outcome (null / empty / typed exception), not a leaked HTTP error.
- **Response validation:** a response violating the OpenAPI schema is rejected (caught, logged) rather than
  mis-mapped.
- **Egress filtering:** the engine's `HttpClient` honors `network.allow` (a call to an undeclared host fails
  before a byte leaves) — shared with §6.
- **JSONata edge cases:** missing optional field → null (not throw); coordinate-order mapping (`[lng,lat]`)
  is correct (the swapped-fixture-must-fail test from §3).

Golden approach: each `(OpenAPI fixture, mapping, recorded response)` → an **approved DTO snapshot**
(Verify), so a mapping change is a reviewable diff.

---

## 6. Host & security tests

Target: `Calendar.Integration.Tests` (host) — the structural sandbox from
[PLUGINS.md §7–§8](PLUGINS.md#8-security--sandboxing) and [ARCHITECTURE.md §17](ARCHITECTURE.md#17-security--privacy).
These are **security-critical** and must fail the build loudly.

| Test | Assertion |
| --- | --- |
| **Egress allowlist enforcement** | A plugin whose `network.allow` is `["api.duffel.com"]` calling `https://evil.example` is **blocked before any byte leaves** — the host `HttpClient` physically cannot reach an undeclared host (PLUGINS §8.2). Test wildcard patterns (`*.caldav.icloud.com`) and the deny path. A manifest with `allow: ["*"]` is flagged by a guard (§7). |
| **Secrets never leave the vault** | A plugin can obtain an `AuthHandle` (scoped, short-lived) but **cannot** read the refresh token, client secret, or the raw vaulted access token; `SecretRef` rows hold only AEAD ciphertext, never plaintext (DATA-SCHEMA §2.2 "hard invariant, CI-asserted"). Audit log redacts `Ciphertext`/`Nonce`. The broker's `ApplyAsync` injects credentials without exposing them. |
| **Capability gating** | A plugin declaring only `geo.geocode` is **not** dispatched for `calendar.read`; the registry only returns it for its declared capabilities; invoking an undeclared capability is refused. |
| **sdkVersion validation** | A manifest requesting an incompatible SDK major is **rejected at the `validate` step** before load (`SdkVersion.IsCompatible`, SDK-CONTRACT §1); `"1.x"`/`"1.2"`/`"1.2.0"` ranges parse and admit/deny correctly. |
| **Signature / trust-tier validation** | An unsigned or tamper-detected plugin in a tier requiring signing is rejected; trust tiers (`InBox`/`Signed`/`Community`/`LocalDev`) gate out-of-process default (PLUGINS §8.3–8.4). |
| **ALC load / unload** | An assembly plugin loads into a **collectible `AssemblyLoadContext`**, registers capabilities, then **unloads cleanly** — assert no assembly leak across repeated load/unload (the hot-upgrade path, PLUGINS §7). |
| **Out-of-process crash isolation** | An out-of-process plugin that crashes (or hangs) does **not** take down the host; the host detects the dead gRPC channel, marks the plugin `Error`, and keeps serving (PLUGINS §7–8). |
| **Resource budgets / circuit breakers** | A plugin exceeding its rate budget is throttled; the breaker opens; paid calls stop when the budget is spent (shared with the aggregator; travel-fares/geo-routing §13/§9). |

---

## 7. Guard / lint tests

Fast, no-Docker, deterministic **policy assertions** that fail the build. Target: `Calendar.Guards.Tests`,
run in the `lint-guards` CI job on every PR. These encode invariants that are otherwise easy to violate
silently.

### 7.1 No public Nominatim / public OSM tile host in any shipped manifest (CRITICAL — PLUGIN-RESEARCH)

The Nominatim usage policy **forbids** building its public endpoint into no-code/LLM-generated generic
geocoding apps (1 req/s, mandatory caching), and the public OSM tile/vector servers are best-effort with no
SLA. So:

```csharp
// Calendar.Guards.Tests — pseudocode
[Fact] public void No_shipped_manifest_references_a_public_geo_endpoint()
{
    var banned = new[] {
        "nominatim.openstreetmap.org",   // public Nominatim — policy-forbidden
        "photon.komoot.io",              // public Photon demo — not for production load
        "tile.openstreetmap.org",        // public OSM raster tiles — no bulk/SLA
        "*.tile.openstreetmap.org",
        "demotiles.maplibre.org",        // demo only (allowed ONLY as the bundled offline fallback, not a preset host)
    };
    foreach (var manifest in AllShippedManifests())             // plugins/**/plugin.yaml
        foreach (var host in manifest.Network.Allow.Concat(manifest.Presets.Hosts()))
            Assert.DoesNotContain(host, banned);                // and self-host baseUrl must be REQUIRED (no public default)
}
```

- Also assert the geo connectors make `baseUrl` **required** (cannot default to a public endpoint) —
  encoding geo-geocoding §4.4/§5.3 and geo-tiles §10 as a CI check.
- Assert **precache is disabled for the public-OSM tiles plugin** (geo-tiles §10 policy compliance).
- **Amadeus is excluded** (self-service sunsets 2026-07-17, PLUGIN-RESEARCH): a guard asserts no shipped
  pricing manifest declares an Amadeus self-service host as primary.
- **Attribution presence:** every geo/tiles preset carries its required attribution string
  (`© OpenStreetMap contributors`, `© MapTiler`, provider credit); a preset missing attribution fails
  (geo-geocoding §11, geo-tiles §10).

### 7.2 Manifest schema validation

- Every `plugin.yaml` validates against the manifest JSON Schema (`PluginManifest` shape, SDK-CONTRACT §2.3):
  required fields present, `capabilities` ⊆ the catalog (`CapabilityIds`), `auth.scheme` ∈ `AuthScheme`,
  `config` is a valid JSON Schema object.
- Each declared `config` schema round-trips through the schema-driven UI generator without error
  (PLUGINS §9).

### 7.3 SDK compatibility

- Every first-party plugin's `sdkVersion` admits `SdkVersion.Current` (SDK-CONTRACT §1).
- An **API-surface / public-API approval test** (e.g. PublicApiGenerator + Verify) on
  `Calendar.Plugin.Abstractions` so any **breaking** change to the SDK surface is a reviewable diff and a
  conscious major bump — enforcing the "additive-only within a major" promise.

### 7.4 DB migration round-trips

- The EF Core migration from the **full** [DATA-SCHEMA](DATA-SCHEMA.md) (all tables, indexes, recurrence &
  sync metadata) applies clean to an empty SQLite DB and **`Up`→`Down`→`Up` round-trips**.
- The model has **no pending model changes** vs the latest migration (`dotnet ef migrations has-pending`-style
  assertion).
- Every index named in DATA-SCHEMA §4 exists (`IX_Event_Calendar_Time`, `IX_Event_Recurring`,
  `IX_Event_DedupSignature`, `UX_GeocodeCache_QueryHash`, `UX_RouteLeg_From_To_Mode`,
  `IX_FareSample_Watch_Time`, `UX_*` upsert keys) — a schema-drift guard.
- SQLCipher: opening the keyed DB with the wrong key fails; with the right key succeeds (DATA-SCHEMA §5.5).

---

## 8. UI / E2E tests

Target: `Calendar.E2E.Tests` — **Playwright for .NET** driving the Blazor WASM PWA + the ASP.NET host
end-to-end (the host can be booted via `WebApplicationFactory` or `dotnet run`; Playwright drives a real
browser). Few, high-value, slower; gated in the `e2e` CI job. Browsers: Chromium (required), Firefox +
WebKit (matrix, like the reference `BlazorPlaywright` sample).

### 8.1 Flows (map to the ROADMAP demos)

| Flow | Steps & assertions |
| --- | --- |
| **Add ICS account → events render** (Phase 1 demo) | Paste an ICS feed URL via the schema-driven "Add account" form → month grid shows the feed's events offline. The first end-to-end proof. |
| **Toggle calendar / category** | Hide a calendar → its events vanish; "hide all Birthdays" one toggle → birthdays vanish regardless of source (visibility pipeline, §2.3). |
| **Duplicate collapse** | Two holiday feeds → duplicates collapse to one "+N"; merge/split override reverses it (ARCHITECTURE §12). |
| **Multi-month range select** | Range-select across month boundaries; the selection persists across view switches **without re-querying providers** (ARCHITECTURE §6). |
| **Map pins + clustering** (Phase 2) | Geocoded events render as pins on the MapLibre world map; clustering works; the date scrubber filters by time; style-switch (light↔dark / provider↔provider) re-adds pin/route layers (geo-tiles §10). |
| **Travel-time conflict** (Phase 2 demo) | Two back-to-back events at different places show a commute chip + "leave by"; an infeasible gap shows the "you can't make it" conflict warning (§2.4). |
| **Fare overlay** (Phase 5) | Multi-month cheapest-date overlay paints; a stale cell is greyed (`Stale`); no live call per cell (overlay batching, travel-fares §13). |

### 8.2 Accessibility checks (UI.md a11y; ARCHITECTURE §6)

- **ARIA grid semantics:** the calendar exposes a proper `role="grid"` with row/column structure; the map
  and sidebar have correct landmarks/labels.
- **Keyboard navigation:** arrow-key movement across the month grid, Enter to open an event, Tab order
  through sidebar → views → grid; range-select reachable by keyboard.
- **Automated a11y scan:** run an axe-core pass (via Playwright) on each main view; fail on serious/critical
  violations. Contrast/attribution-control visibility (attribution can't be hidden, geo-tiles §10).

> Tile/style/PMTiles failure modes (404/5xx → bundled default, tile 429 toast, PMTiles Range `206`, offline
> cached-vs-uncached panning) are best asserted here in a real MapLibre canvas, complementing the §4 stubs
> (geo-tiles §10).

---

## 9. Test data & fixtures strategy

A single, version-controlled fixtures tree so every layer feeds from the same canonical inputs.

```
tests/
├─ fixtures/
│  ├─ ics/            # canned .ics: holidays, birthdays, recurrence/EXDATE/RDATE/RECURRENCE-ID,
│  │                  #   all-day single+multi-day, DST, non-UTC VTIMEZONE, malformed/truncated,
│  │                  #   Proton Full, Proton Limited, TripIt detailed feed
│  ├─ google/         # recorded events.list / sync pages: delta, 410, pagination, cancelled instance
│  ├─ graph/          # recorded delta pages: @removed, exception, canceled occurrence, 429+Retry-After
│  ├─ caldav/         # PROPFIND / REPORT / sync-collection + CTag-fallback XML bodies + Multi-Status partials
│  ├─ geo/            # geocode (ambiguous, non-Latin, [lng,lat]), route (no-route, ferry, polyline p5/p6)
│  ├─ fares/          # Duffel test-mode routes, Kiwi Tequila responses, expiry, empty, 429+ratelimit-reset
│  ├─ openapi/        # sample OpenAPI specs + JSONata mappings for connector-engine tests (§5)
│  └─ manifests/      # valid + deliberately-invalid plugin.yaml for guard tests (§7)
└─ __snapshots__/     # Verify approved goldens: recurrence expansion, normalized DTOs, aggregator records
   (.verified.txt / .verified.json committed; .received.* git-ignored)
```

Principles:

- **VCR-style recorded provider payloads.** Real provider responses are captured **once** (against a dev
  account / sandbox / test mode), scrubbed of secrets, committed, and replayed via WireMock.Net / static
  fixture server. CI **never** hits the live provider. Re-record deliberately when an API changes; the diff
  is reviewed.
- **Golden files via Verify.** `.verified.*` files are the source of truth for canonical shapes (§2.2, §3,
  §5, §2.6). Updating a golden is an explicit `*.received.*`→approve step in review.
- **Container seeding.** CalDAV containers (Radicale/Nextcloud) are seeded by `PUT`-ing the same `ics/`
  fixtures at container start (collection fixture); Nominatim/Photon/OSRM use a **small country extract**
  (e.g. a single region from Geofabrik) baked into a cached image, not the planet — keeps CI fast.
- **Deterministic clock & ids.** A frozen `TimeProvider` and a seeded id generator so snapshots are stable;
  `RetrievedAt`/`expires_at` in fare fixtures are relative to the frozen clock.
- **Secret hygiene in fixtures.** A guard scans fixtures for accidental tokens/keys (high-entropy strings),
  complementing §6's vault invariant.

---

## 10. CI pipeline

A **GitHub Actions** workflow with parallel jobs mapped to the pyramid. Fast jobs gate every PR; slow
Docker/browser jobs are required for merge but can be sharded. Tie-in to ROADMAP: the **`unit` + `lint-guards`
jobs encode the "Cross-cutting, every phase" items** and run from **Phase 0**; `integration`/`e2e` light up
as the relevant plugins/views land (Phase 1+).

### 10.1 Workflow sketch

```yaml
name: ci
on: { pull_request: {}, push: { branches: [main] } }

permissions: { contents: read }

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - uses: actions/cache@v4               # cache NuGet (~/.nuget/packages) keyed on lock files
        with: { path: ~/.nuget/packages, key: nuget-${{ hashFiles('**/*.csproj','**/packages.lock.json') }} }
      - run: dotnet restore
      - run: dotnet build -c Release --no-restore

  unit:                                       # Calendar.Domain.Tests + Application + (in-proc) Contract
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet test tests/Calendar.Domain.Tests tests/Calendar.Application.Tests
             -c Release --collect:"XPlat Code Coverage"
      - uses: actions/upload-artifact@v4
        with: { name: coverage-unit, path: '**/coverage.cobertura.xml' }

  lint-guards:                                # Calendar.Guards.Tests — manifests, schema, SDK compat, migrations
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet test tests/Calendar.Guards.Tests -c Release
      # includes: no-public-Nominatim/OSM guard, manifest schema, SDK public-API approval, EF migration round-trip

  integration-with-services:                  # Calendar.Integration.Tests — Testcontainers + WireMock.Net
    needs: build
    runs-on: ubuntu-latest                    # Linux runner has Docker; Testcontainers manages lifecycles
    strategy:
      matrix: { suite: [caldav, geo, fares-graph-google, host-security, connector-engine] }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet test --filter "Category=Integration&Suite=${{ matrix.suite }}"
             -c Release --collect:"XPlat Code Coverage"
      - uses: actions/upload-artifact@v4
        with: { name: coverage-int-${{ matrix.suite }}, path: '**/coverage.cobertura.xml' }

  e2e:                                         # Calendar.E2E.Tests — Playwright (host + browser)
    needs: build
    runs-on: ubuntu-latest
    strategy: { matrix: { browser: [chromium, firefox, webkit] } }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet build -c Release tests/Calendar.E2E.Tests
      - run: pwsh tests/Calendar.E2E.Tests/bin/Release/net9.0/playwright.ps1 install --with-deps ${{ matrix.browser }}
      - run: dotnet test tests/Calendar.E2E.Tests -c Release
        env: { BROWSER: ${{ matrix.browser }} }

  coverage-gate:
    needs: [unit, integration-with-services]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
      - run: |   # merge cobertura, enforce threshold (e.g. ReportGenerator + a min-line/branch gate)
          echo "fail the job if domain-layer line coverage < target"

  security-scan:
    needs: build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet restore
      - run: dotnet list package --vulnerable --include-transitive   # dependency CVEs → fail on any
      # plus: CodeQL (github/codeql-action) for C#, and a secret-scan over fixtures (§9)
```

### 10.2 Required checks & policy

- **Required for merge:** `build`, `unit`, `lint-guards`, `integration-with-services`, `coverage-gate`,
  `security-scan`. `e2e` required on `main` (and PRs touching `Web`); may run nightly for the full
  3-browser matrix to keep PRs fast.
- **Caching:** NuGet (`~/.nuget/packages`), Playwright browser binaries, and the **prebuilt geo container
  images** (Nominatim/Photon/OSRM with a baked country extract) — the slowest part of `integration`.
- **Matrix:** `integration` sharded by suite (parallel containers); `e2e` by browser. Optionally a
  `windows-latest` leg for the Windows path (this repo is developed on Windows).
- **Coverage gate:** enforce a **line/branch threshold on `Calendar.Domain`** (where it matters most);
  treat integration/E2E coverage as informational.

### 10.3 ROADMAP tie-in (per-phase cross-cutting tests)

| Phase | New tests that land with the phase |
| --- | --- |
| **0** | `unit` + `lint-guards` jobs live; recurrence/dedup/visibility/route-gap **unit skeletons**; **migration round-trip** guard for the full schema; trivial-plugin **ALC load/unload** test; SDK public-API approval baseline. |
| **1 (ICS)** | ICS contract tests + static-fixture-server integration; duplicate-collapse + visibility **E2E**; the recurrence golden-file suite fills out. |
| **2 (geo)** | Nominatim/Photon/OSRM/Valhalla **containers**; geocode-cache + polyline + feasibility tests; **no-public-Nominatim/OSM guard** becomes load-bearing; map-pin + travel-conflict **E2E**. |
| **3 (Google+MS)** | Google/Graph **recorded-fixture** integration (`410`, backoff, paging-guard); **mock-OAuth-IdP broker** tests; **secrets-never-leave-vault** host test. |
| **4 (CalDAV)** | Radicale + Nextcloud containers; `sync-collection` vs CTag-fallback parity; Multi-Status partials. |
| **5 (travel)** | Duffel test-mode + Kiwi-fixture pricing contract; offer-expiry / dedupe / stale-history / threshold-firing; fare-overlay **E2E**. |
| **6 (sharing/sync/write)** | `ICalendarWriter` `412` contract; tokenized-share-feed tests; E2E cloud-sync LWW; out-of-process untrusted-plugin **crash isolation**; offline write-outbox replay. |

---

## 11. Prioritization — what to build first

A practical order so the highest-leverage, cheapest tests exist from day one.

**Phase 0 (build immediately — foundational, no Docker):**

1. **Recurrence golden-file harness (Verify) + the §2.2 matrix.** Highest bug density across four plugins;
   write once, reuse everywhere.
2. **Dedup + visibility + travel-gap unit tests (§2.1, §2.3, §2.4).** The ROADMAP "every phase" core; pure,
   fast, gate every PR.
3. **Guard tests (§7):** no-public-Nominatim/OSM (CRITICAL), manifest-schema, **EF migration round-trip**,
   SDK public-API approval. These are cheap and protect invariants from the first commit.
4. **The `ICalendarSourceContract` skeleton (§3.1)** with the trivial test plugin — so every later plugin
   plugs into a ready suite.
5. **`unit` + `lint-guards` CI jobs** wired green.

**Phase 1–2 (first integration + first E2E):**

6. **ICS static-fixture-server integration + ICS contract** (the first real plugin, no OAuth).
7. **First Playwright E2E:** add-ICS-account → render, duplicate collapse, category toggle.
8. **Connector-engine tests (§5)** + the **geo containers** (Nominatim/Photon/OSRM) — the engine is shared
   leverage and the geo guard becomes load-bearing.

**Phase 3+ (as the surface grows):**

9. Mock-OAuth-IdP broker tests + Google/Graph recorded fixtures + the **host-security suite** (§6:
   egress, secrets, capability gating, ALC, crash isolation).
10. CalDAV containers; travel/fares contract + stale-history; sharing/cloud-sync/write-back; the full
    3-browser E2E matrix and coverage gate tightening.

**Guiding rule:** every new integration ships **with** its contract test (§3) and rides the existing shared
suites — the core never grows a special case to add a provider, and neither does the test suite.

---

## Sources

Test notes consolidated from the in-repo deep dives (each `## Failure modes & tests` section):
[caldav-plugin.md §13](deep-dives/caldav-plugin.md), [google-calendar-plugin.md §15](deep-dives/google-calendar-plugin.md),
[microsoft-graph-plugin.md §14](deep-dives/microsoft-graph-plugin.md), [ics-plugin.md §16](deep-dives/ics-plugin.md),
[geo-routing-plugin.md §9](deep-dives/geo-routing-plugin.md), [geo-geocoding-places-plugin.md §11](deep-dives/geo-geocoding-places-plugin.md),
[geo-tiles-plugin.md §10](deep-dives/geo-tiles-plugin.md), [travel-fares-plugin.md §13](deep-dives/travel-fares-plugin.md);
plus [ARCHITECTURE.md](ARCHITECTURE.md), [DATA-SCHEMA.md](DATA-SCHEMA.md), [SDK-CONTRACT.md](SDK-CONTRACT.md),
[PLUGINS.md](PLUGINS.md), [PLUGIN-RESEARCH.md](PLUGIN-RESEARCH.md), [ROADMAP.md](ROADMAP.md).

External tooling references:

- WireMock.Net (HTTP mocking, OpenAPI stubbing, Testcontainers module): <https://github.com/WireMock-Net/WireMock.Net>, <https://wiremock.org/dotnet/using-wiremock-net-testcontainers/>, <https://www.nuget.org/packages/WireMock.Net.Testcontainers>
- Testcontainers for .NET: <https://dotnet.testcontainers.org/>
- Playwright for .NET (Blazor WASM E2E, multi-browser GitHub Actions): <https://playwright.dev/dotnet/>, <https://github.com/pekspro/BlazorPlaywright>
- Verify (snapshot / golden-file testing): <https://github.com/VerifyTests/Verify>
- xUnit: <https://xunit.net/>
- Nominatim usage policy (public-endpoint prohibition driving the §7 guard): <https://operations.osmfoundation.org/policies/nominatim/>
- OSMF tile usage policy: <https://operations.osmfoundation.org/policies/tiles/>
- Radicale (CalDAV test server): <https://radicale.org/>; OSRM: <https://project-osrm.org/>; Photon: <https://github.com/komoot/photon>
