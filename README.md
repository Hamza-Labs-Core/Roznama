# Unified Calendar

A privacy-respecting calendar that **aggregates events from every account you own** — Google,
Microsoft/Outlook (work), Proton, iCloud, Fastmail, Nextcloud, and any public ICS feed — into a single
**planning UI** with a **world-map view** and **travel-time-aware scheduling**. Toggle calendars on/off,
group duplicate events (holidays, birthdays), hide whole categories, plan vacations across many months
at once, see prices and directions, and share calendars with others.

Its defining trait: **nothing is hardcoded.** Every integration — calendars, travel, fares, map tiles,
geocoding, routing — is a **plugin** discovered at runtime via a **capability** system. Built-in
providers ship as first-party plugins; new APIs can be added declaratively (an OpenAPI spec + a field
mapping) with **no code**.

> **Status:** Design phase. This repository currently contains architecture and roadmap docs.
> No application code has been written yet — see [Roadmap](docs/ROADMAP.md).

## At a glance

| Decision | Choice |
| --- | --- |
| Platform | Responsive web app (Blazor WebAssembly PWA, installable + offline) |
| Stack | .NET 9 / C# — ASP.NET Core API + Blazor WASM front end |
| Extensibility | **Plugin + capability system** — nothing hardcoded; declarative (OpenAPI) or assembly plugins |
| Data model | Hybrid **local-first**: SQLite on-device, optional **end-to-end-encrypted** cloud sync |
| Maps | MapLibre GL (open/self-hostable); Google/Mapbox tiles, geocoding, routing as plugins |
| Privacy | Host owns all secrets; read-only scopes first; no data leaves the device unless sync is enabled |

## Core capabilities

- **The planning UI** — month, **multi-month** (vacation planning), week, day, agenda, and **map** views
  over one unified, deduped event stream.
- **Connect any account** — Google API, Microsoft Graph, CalDAV (iCloud/Fastmail/Nextcloud/Proton*), ICS.
- **Show / hide** calendars and **hide whole categories** (e.g. Birthdays) with one switch.
- **Duplicate detection & grouping** — the same holiday/birthday across accounts collapses to one entry,
  explainably and reversibly.
- **World-map view** — see event/trip locations on a map with clustering, a date scrubber, and trip routes.
- **Directions & travel time** — commute time between consecutive events, "leave by" hints, and
  "you can't make it" conflict warnings (Google Maps / OSRM / others, pluggable).
- **Travel** — import bookings (TripIt) as events; track **flight & hotel prices** with cheapest-date
  overlays on the planner.
- **Sharing** — publish read-only calendar links or free/busy availability.

\* Proton has no public CalDAV/API today — see the [providers note](docs/ARCHITECTURE.md#11-calendar-capabilities--iosicloud).

## Documents

- [Architecture](docs/ARCHITECTURE.md) — system design, capability catalog, domain model, geo, security.
- [Plugins](docs/PLUGINS.md) — the SDK contract overview, manifest spec, declarative connectors, sandboxing.
- [Plugin host](docs/PLUGIN-HOST.md) — the host subsystem: loading/ALC isolation, connector engine, auth broker, sandboxing.
- [SDK contract](docs/SDK-CONTRACT.md) — the authoritative `Calendar.Plugin.Abstractions` surface (interfaces + DTOs).
- [Data schema](docs/DATA-SCHEMA.md) — EF Core / SQLite tables, indexes, recurrence & sync metadata.
- [UI & planning](docs/UI.md) — layout, views, map, travel-time, overlays, performance, a11y.
- [UI wireframes](docs/UI-WIREFRAMES.md) — low-fi ASCII wireframes for every screen + interactions.
- [API contracts](docs/API.md) — the host ⇄ UI REST surface.
- [Plugin research](docs/PLUGIN-RESEARCH.md) — per-provider capabilities & blockers (current as of 2026-06).
- [Plugin deep dives](docs/deep-dives/README.md) — end-to-end specs for all 8 first-party plugins.
- [Testing & CI](docs/TESTING.md) — test pyramid, contract/integration/E2E strategy, guard tests, CI pipeline.
- [Decision records](docs/adr/README.md) — the 13 load-bearing architecture decisions (ADRs).
- [Roadmap](docs/ROADMAP.md) — phased delivery, plugin-first and UI-first.
