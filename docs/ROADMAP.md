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

- [ ] Create `Calendar.sln` + projects from [Solution layout](ARCHITECTURE.md#18-solution-layout),
      including **`Calendar.Plugin.Abstractions`** first — implement it straight from [SDK-CONTRACT.md](SDK-CONTRACT.md).
- [ ] **Plugin registry + loader** per the [plugin host design](PLUGIN-HOST.md): `AssemblyLoadContext`
      for assembly plugins, manifest parsing, capability registration. Stub connector engine.
- [ ] EF Core + SQLite; first migration from the full [data schema](DATA-SCHEMA.md)
      (all tables, indexes, recurrence & sync metadata).
- [ ] Blazor WASM shell per the [wireframes](UI-WIREFRAMES.md): sidebar (accounts/calendars/categories)
      + empty month grid + view switcher.
- [ ] Unit-test project + CI (build + test).

**Done when:** the app launches, the registry can load a trivial plugin, and tests run green.

## Phase 1 — UI slice via the ICS plugin (no OAuth)
**Goal:** real events end-to-end, proving the UI and the plugin model together.

- [ ] **ICS plugin** (`calendar.read`, declarative-ish via Ical.Net): fetch feed, parse, expand RRULE.
- [ ] Schema-driven **"Add account"** flow (paste an ICS URL — holidays, birthdays).
- [ ] **Month + Multi-month** views from the normalized stream.
- [ ] Per-calendar **show/hide** + **category** hide (Holidays, Birthdays).
- [ ] **Duplicate detection & grouping** with merge/split overrides.

**Done when:** two holiday feeds render, duplicates collapse, birthdays hide with one switch — offline.
**This is the first demo.**

## Phase 2 — Map, geocoding & travel-time
**Goal:** the map view and travel-aware scheduling — early, because it's a defining feature.

- [ ] **`geo.tiles` (MapLibre/OSM)** + **Map view**: pins, clustering, date scrubber.
- [ ] **`geo.geocode` (Nominatim)** with `GeocodeCache`; resolve event locations to `Place`.
- [ ] **`geo.route` (OSRM/Valhalla self-host)** + **travel-time chips**, **leave-by**, **conflict
      warnings**, optional buffer events.
- [ ] **Fallback aggregator** generalized for `geo.route`/`geo.geocode`.

**Done when:** events appear on a world map and back-to-back events show commute time + "you can't make it."

## Phase 3 — OAuth calendar plugins (Google + Microsoft)
**Goal:** the two biggest real accounts, via the auth broker.

- [ ] **OAuth broker + encrypted token vault** (PKCE, refresh, client-credentials).
- [ ] **Google plugin** (`calendar.read`, sync tokens, Holidays/Birthdays).
- [ ] **Microsoft Graph plugin** (MSAL, delta, work/school).
- [ ] Background **sync engine** (Quartz) with backoff/rate limits.
- [ ] Account priority ordering → dedup canonical selection.

**Done when:** Google + Outlook/work sync incrementally and merge with ICS feeds and the map.

## Phase 4 — CalDAV & iOS/iCloud
**Goal:** open-standard + Apple accounts.

- [ ] **CalDAV plugin** (`PROPFIND` + `calendar-query` `REPORT` + `sync-collection`).
- [ ] Presets: iCloud (app-specific password), Fastmail, Nextcloud.
- [ ] Proton via read-only **"share via link" ICS URL** (ICS plugin). Track upstream for a real API.
- [ ] (Optional) **EventKit iOS companion** plugin for device-local "On My iPhone" calendars.

**Done when:** an iCloud/Nextcloud calendar appears alongside the rest.

## Phase 5 — Travel: itineraries, stays & fares
**Goal:** trip planning on the calendar and map.

- [ ] **`itinerary.import`**: TripIt via its **`.ics` feed** (public API is closed to new integrations) → `Trip`/`TripItem` → events.
- [ ] **`flight.price` / `stay.price`** plugins — **Duffel** (Flights + Stays) primary, Kiwi as fallback (⚠️ **not** Amadeus — self-service sunsets 2026-07-17) behind the aggregator.
- [ ] Multi-month **cheapest-date** (flights) + **nightly-rate** (hotels) overlays.
- [ ] **Fare watches** + price history + `notify` on drops.
- [ ] Trips drawn as **routes on the map**.

**Done when:** you plan a vacation across months, see prices and availability, and trips show on the map.

## Phase 6 — Sharing, cloud sync & polish
**Goal:** collaboration, multi-device, and the long tail.

- [ ] **Sharing**: tokenized read-only ICS links, `FullDetails` vs `FreeBusy`, expiry/revocation.
- [ ] **Optional E2E-encrypted cloud sync node** (multi-device + remote share relay).
- [ ] **Write-back** (`calendar.write`): create/edit/delete to providers; PWA offline write queue.
- [ ] **Plugin marketplace**: install signed plugins from URL/registry; out-of-process untrusted plugins.
- [ ] Notifications/reminders, search refinements, import/export, a11y + theming pass.

---

### Cross-cutting, every phase
- Tests per the [testing strategy](TESTING.md): dedup engine, recurrence expansion, visibility pipeline,
  route-gap logic, the connector engine, and the manifest guard tests.
- Least-privilege scopes; host-owned secrets; per-plugin egress allowlist + budgets.
- Every new integration is a **plugin** behind the SDK — the core never changes to add one.
