# UI & planning

The UI is the product. Everything else — sync, plugins, geo — exists to feed a fast, fluid surface
where you **see** your whole life across calendars and **plan** over it: vacations across months, trips
on a world map, and schedules that understand travel time. This document specifies the layout, views,
interaction model, and the quality bars that make it feel instant.

- [1. Goals](#1-goals)
- [2. Layout](#2-layout)
- [3. Views](#3-views)
- [4. The planning model](#4-the-planning-model)
- [5. Map view](#5-map-view)
- [6. Travel-time & directions](#6-travel-time--directions)
- [7. Overlays (free/busy & prices)](#7-overlays-freebusy--prices)
- [8. Interaction details](#8-interaction-details)
- [9. Performance](#9-performance)
- [10. Accessibility, theming & offline](#10-accessibility-theming--offline)

---

## 1. Goals

- **See everything at once, clearly.** Many accounts, one coherent timeline; color by source, filter by
  calendar/category, duplicates collapsed.
- **Plan, don't just view.** Drag to create/move, select date ranges across months, sketch tentative
  trips in a scenario layer, and let the app reason about travel time, conflicts, and prices.
- **Instant & offline.** No spinner between views; works on a plane.

## 2. Layout

```
┌──────────────┬───────────────────────────────────────────────┬──────────────┐
│  SIDEBAR     │  VIEW CANVAS                                    │  INSPECTOR   │
│              │  ┌─────────────────────────────────────────┐   │  (event /    │
│  Accounts ▸  │  │ toolbar: view switch · date nav · search │   │   trip /     │
│   ▢ Personal │  ├─────────────────────────────────────────┤   │   place      │
│   ▢ Work     │  │                                          │   │   details,   │
│   ▢ Public   │  │   Month / Multi-month / Week / Day /     │   │   travel     │
│              │  │   Agenda / MAP                           │   │   time,      │
│  Calendars ▸ │  │                                          │   │   prices)    │
│   ☑ Holidays │  │                                          │   │              │
│   ☑ Birthdays│  └─────────────────────────────────────────┘   │              │
│  Categories ▸│                                                 │              │
│   ☑ Work     │                                                 │              │
│   ☐ Birthdays│  (collapsible panels; mobile = bottom sheets)   │              │
└──────────────┴───────────────────────────────────────────────┴──────────────┘
```

- **Sidebar** — three grouped toggle lists: **Accounts**, **Calendars** (show/hide each), **Categories**
  (hide whole classes like Birthdays). Reorder accounts here to set **dedup priority**.
- **View canvas** — the active view; switching is instant (shared event stream).
- **Inspector** — context panel for the selected event/trip/place, including travel-time and price info.
- **Responsive** — panels collapse to bottom sheets on mobile; the canvas is always primary.

## 3. Views

| View | Purpose |
| --- | --- |
| **Month** | Classic grid; event chips colored by source calendar. |
| **Multi-month** | N month grids in a responsive flow (e.g. 3×4 = a year). The **vacation-planning** surface: range selection across months, price + free/busy overlays. |
| **Week / Day** | Time-grid detail; travel-time gaps shown between events. |
| **Agenda** | Dense chronological list; great on mobile and for search results. |
| **Map** | World map of event/trip locations — see §5. |

All views render from **one** filtered, deduped, recurrence-expanded event stream. Changing view never
re-queries providers; it re-projects the same in-memory data.

## 4. The planning model

Planning is more than CRUD — it's a first-class layer:

- **Range selection** across month boundaries (multi-month) → acts as the planning window for prices,
  free/busy, and "find an open week."
- **Scenario / draft layer.** Tentative items (a candidate trip, a maybe-meeting) live in a separate
  visual layer (dashed, distinct). You can build a whole vacation as drafts, compare options, then
  **commit** drafts into real events (writing back once `calendar.write` is enabled). Scenarios are
  user metadata and **sync** like other preferences.
- **Conflict & gap awareness.** The planner service continuously evaluates overlaps and travel-time gaps
  (§6) for the visible range and annotates the grid.

## 5. Map view

A world map (MapLibre GL via JS interop) that answers *"where am I, over time?"*

- **Pins** for every event/trip with a resolved `Place` (geocoded + cached). **Clustering** at low zoom.
- **Date-range scrubber** synced to the current selection — drag it to watch pins appear/disappear over
  time; ideal for reviewing or planning travel.
- **Trip routes drawn** between consecutive legs (flight arcs, drive lines) using `geo.route` geometry.
- **Geographic filtering** — draw/zoom to a region to filter the other views to events there.
- **Click-to-place** — drop a pin to set an event/draft location; reverse-geocoded to an address.
- **Pluggable tiles** — `geo.tiles` plugin selects the basemap: OSM/MapLibre by default, Google or
  Mapbox if those plugins are installed. Self-hosted tiles keep it fully private.

## 6. Travel-time & directions

The headline scheduling feature: the calendar understands how long it takes to get between places.

- Between two **consecutive events at different locations**, the planner calls `geo.route` (through the
  fallback aggregator) for the chosen **mode** (drive / transit / walk / bike), using the **event time**
  (transit respects schedules via the arrival time).
- Surfaced as:
  - a **commute chip** in the gap — e.g. `🚗 32 min`,
  - a **"leave by 14:05"** hint on the earlier event,
  - a **conflict warning** when `gap < commute` — *"you can't make it"* — with a suggested fix,
  - an optional **auto-inserted travel buffer** event so the time is visibly blocked.
- Mode and default buffers are per-user settings; results are cached as `ROUTE_LEG` rows and recomputed
  only when an endpoint/time changes.

## 7. Overlays (free/busy & prices)

Toggleable layers painted onto Month/Multi-month/Week:

- **Free/busy** — combined availability across all visible calendars; shaded busy blocks make open
  windows obvious for vacation or meeting planning.
- **Flight cheapest-date** — for a watched route, a price badge per candidate date (from `flight.price`).
- **Hotel nightly rate** — for a destination + window, nightly price overlay (from `stay.price`).
- Overlays are non-destructive layers above the event grid; toggle them per planning session.

## 8. Interaction details

- **Drag-create** on empty grid; **drag-move / resize** existing events (write-back gated).
- **Keyboard-first**: `T` today, `M/W/D/A/G` switch views, `←/→` navigate, `/` search, `N` new event.
- **Search** across all sources (title, location, attendee) → Agenda results; click to jump in context.
- **Duplicate affordance**: a grouped event shows `+N duplicates`; expand to see sources and
  **merge / split / never-merge**.
- **Inspector actions**: change color/category, set/clear location (geocode), pick travel mode, watch a
  fare, share.

## 9. Performance

- **On-demand recurrence expansion** for the visible range only (Ical.Net) — the DB never stores
  occurrences.
- **Virtualized rendering** for long agenda/multi-month; only visible cells mount.
- **Client cache** (IndexedDB) of the projected event stream → instant cold start and offline view.
- **Incremental projection**: a sync delta updates only affected cells/legs, not a full re-render.
- **Map**: vector tiles + clustering keep thousands of pins smooth; route geometry cached per leg.

## 10. Accessibility, theming & offline

- **A11y**: full keyboard nav, ARIA grid semantics for the calendar, focus management, sufficient
  contrast; color is never the only signal (icons/labels accompany calendar colors).
- **Theming**: light/dark + custom accent; per-calendar colors are user-overridable.
- **Offline (PWA)**: service worker caches the app shell + last projection; edits queue and replay when
  back online (with `calendar.write`). The map degrades to cached tiles for visited areas.
