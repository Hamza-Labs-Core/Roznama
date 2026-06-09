# Roadmap

Phased delivery from an empty solution to the full feature set. Two principles shape the order:

1. **The plugin host comes first** — because nothing is hardcoded, the capability/plugin system is
   foundational, not an add-on.
2. **The UI leads** — the planning surface is the product, so a real, usable view appears early via the
   zero-OAuth ICS plugin.

Each phase ends with something you can run.

---

## Phase 0 — Foundations & plugin host
**Goal:** a runnable skeleton where capabilities, not providers, drive everything.

- [x] Create `Calendar.sln` + projects from [Solution layout](ARCHITECTURE.md#18-solution-layout),
      including **`Calendar.Plugin.Abstractions`** first — implement it straight from [SDK-CONTRACT.md](SDK-CONTRACT.md).
- [x] **Plugin registry + loader** per the [plugin host design](PLUGIN-HOST.md): `AssemblyLoadContext`
      for assembly plugins, manifest parsing, capability registration. Stub connector engine.
- [x] EF Core + SQLite; first migration from the full [data schema](DATA-SCHEMA.md)
      (all tables, indexes, recurrence & sync metadata).
- [x] Blazor WASM shell per the [wireframes](UI-WIREFRAMES.md): sidebar (accounts/calendars/categories)
      + empty month grid + view switcher.
- [x] Unit-test project + CI (build + test).

**Done when:** the app launches, the registry can load a trivial plugin, and tests run green. ✅

## Phase 1 — UI slice via the ICS plugin (no OAuth)
**Goal:** real events end-to-end, proving the UI and the plugin model together.

- [x] **ICS plugin** (`calendar.read`, via Ical.Net): fetch (conditional GET) feed, parse, diff, expand RRULE.
- [x] **"Add account"** flow (paste an ICS URL — holidays, birthdays).
- [x] **Month + Multi-month** views from the normalized stream.
- [x] Per-calendar **show/hide** + **category** hide (Holidays, Birthdays).
- [x] **Duplicate detection & grouping** (auto canonical by account priority). *Merge/split override UI — follow-up.*

**Done when:** two holiday feeds render, duplicates collapse, birthdays hide with one switch — offline. ✅
**This is the first demo.**

## Phase 2 — Map, geocoding & travel-time
**Goal:** the map view and travel-aware scheduling — early, because it's a defining feature.

- [x] **`geo.tiles` (MapLibre/OSM)** + **Map view**: pins, clustering, date scrubber.
- [x] **`geo.geocode` (Nominatim)** with `GeocodeCache`; resolve event locations to `Place`.
- [x] **`geo.route` (OSRM self-host)** + host-computed **leave-by** / **conflict** (`RouteService`,
      `GET /events/{id}/commute`). *Buffer-event insertion + week/day commute chips — follow-up.*
- [x] **Fallback aggregator** generalized for `geo.route`/`geo.geocode`/`*.price`.

**Done when:** events appear on a world map and back-to-back events show commute time + "you can't make it." ✅
*(Geocode/route/tiles run against self-hosted Nominatim/OSRM/tileserver endpoints — code complete, verified against stubs.)*

## Phase 3 — OAuth calendar plugins (Google + Microsoft)
**Goal:** the two biggest real accounts, via the auth broker.

- [x] **OAuth broker + encrypted token vault** (AES-GCM; PKCE, refresh, client-credentials).
- [x] **Google plugin** (`calendar.read`, sync tokens, Holidays/Birthdays).
- [x] **Microsoft Graph plugin** (delta, work/school).
- [x] Background **sync engine**: hosted sweep ticker + per-account cadence (`NextRunAtUtc`) and
      exponential backoff (`BackoffUntilUtc`/`Attempts`); `GET /sync/status`, `POST /sync/run`,
      `POST /accounts/{id}/sync`.
- [x] Account priority ordering → dedup canonical selection.

**Done when:** Google + Outlook/work sync incrementally and merge with ICS feeds and the map. ✅
*(Connect flow is live end-to-end — `POST /accounts` → authChallenge → shared OAuth callback, with "Connect
Google/Microsoft" buttons in the sidebar. Going live needs each provider's client id in
`OAuthClients:{pluginId}` config; verified against stub token endpoints.)*

## Phase 4 — CalDAV & iOS/iCloud
**Goal:** open-standard + Apple accounts.

- [x] **CalDAV plugin** (`PROPFIND` + `calendar-query` `REPORT` + `sync-collection`, read + write).
- [x] Presets: iCloud (app-specific password), Fastmail, Nextcloud.
- [x] Proton via read-only **"share via link" ICS URL** (handled by the ICS plugin — any feed URL).
- [ ] (Optional) **EventKit iOS companion** plugin for device-local "On My iPhone" calendars.

**Done when:** an iCloud/Nextcloud calendar appears alongside the rest. ✅
*(Connectable from the sidebar: preset/server URL + username + app-specific password; the password is
AEAD-vaulted. Verified against a stub WebDAV server.)*

## Phase 5 — Travel: itineraries, stays & fares
**Goal:** trip planning on the calendar and map.

- [x] **`itinerary.import`**: TripIt via its **`.ics` feed** (handled by the ICS plugin, forced `Travel` category).
- [x] **`flight.price` / `stay.price`** — **Duffel** (Flights + Stays) behind the aggregator. *Kiwi as a second source is a drop-in additional plugin.*
- [x] Multi-month **cheapest-date** (flights) + **nightly-rate** (hotels) overlays.
- [x] **Fare watches** + price history + notify on drops (in-app `NotificationLog`).
- [x] Trips drawn as **routes on the map**: Travel-category stops → ordered legs (`GET /map/trips`),
      road geometry via `geo.route` + the `RouteLeg` cache, dashed straight lines as the fallback.

**Done when:** you plan a vacation across months, see prices and availability, and trips show on the map. ✅
*(Live fares need a Duffel API token — code complete, verified against stubs; degrades to empty without a key.)*

## Phase 6 — Sharing, cloud sync & polish
**Goal:** collaboration, multi-device, and the long tail.

- [x] **Sharing**: tokenized read-only ICS links, `FullDetails` vs `FreeBusy`, expiry/revocation.
- [x] **Optional E2E-encrypted cloud sync node**: every host doubles as a token-gated `/relay/*` blob node;
      devices push/pull AES-GCM change sets (PBKDF2 passphrase key, client-side) with LWW merge + tombstones.
      Synced: accounts (sans credentials), calendars, categories, places, shares, fare watches.
- [x] **Write-back** (`calendar.write`): create/edit/delete to CalDAV; offline `WriteOutbox` queue + replay.
- [x] **Plugin marketplace**: registry index (`GET /marketplace`), install from URL with sha256 +
      ECDSA-P256 detached-signature gates (`POST /plugins/install`, trust-tiered: Signed in-proc,
      unsigned only behind `Marketplace:AllowUnsigned` local-dev), hot load/unload, uninstall.
      *Out-of-process sandbox for untrusted plugins — still deferred per ADR-0007 §8.4.*
- [x] Notifications (fare-drop + event **reminders** via the hosted sweep), **search** (`GET /search` +
      toolbar box), **ICS import/export** (`POST /import` snapshot calendars, `GET /export.ics`),
      dark/light theming. *Full a11y pass — remaining.*

---

### Cross-cutting, every phase
- Tests per the [testing strategy](TESTING.md): dedup engine, recurrence expansion, visibility pipeline,
  route-gap logic, the connector engine, and the manifest guard tests.
- Least-privilege scopes; host-owned secrets; per-plugin egress allowlist + budgets.
- Every new integration is a **plugin** behind the SDK — the core never changes to add one.
