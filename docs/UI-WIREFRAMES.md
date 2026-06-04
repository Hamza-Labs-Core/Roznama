# UI Wireframes (low-fidelity)

Low-fidelity ASCII wireframes for the Unified Calendar Blazor WASM PWA. These are **design-phase
artifacts** — layout intent, states, and interaction annotations only. They are not pixel specs and
contain no app code. Every screen ties back to the authoritative [UI.md](UI.md) spec, the
[ARCHITECTURE.md](ARCHITECTURE.md) §6/§7 summaries, the [API.md](API.md) endpoints that feed it, and
the schema-driven plugin form in [PLUGINS.md §9](PLUGINS.md#9-schema-driven-configuration-ui).

Each wireframe is followed by **numbered interaction annotations**: what is clickable, the visual
states, the data source (API endpoint), and the keyboard shortcut where one applies.

---

## Legend

```text
SYMBOL        MEANING
┌ ─ ┐ │ └ ┘   panel / container borders
├ ┤ ┬ ┴ ┼     border joins / grid rules
▸ ▾           disclosure: collapsed (▸) / expanded (▾)
☑ ☐           checkbox: checked / unchecked (toggle visibility)
▢             tri-state / mixed group toggle
◉ ○           radio: selected / unselected
⠿  ⣿          drag handle (reorder) ; busy/free-busy shading fill
▓ ▒ ░         heavy/medium/light shading (free-busy busy density)
█▌            solid event chip (colored by source calendar)
┄ ┄ (dashed)  scenario / draft layer (tentative, not committed)
●             map pin (single) ; ◎ = cluster pin with count
⟶ ⤢ ◠         route line / flight arc between legs
🚗 🚆 🚶 🚲    travel mode (drive / transit / walk / bike)
⚑             "leave by" hint flag
⚠             conflict / "you can't make it" warning
+N more       overflow affordance (more events than fit)
+N duplicates collapsed duplicate-group affordance
◐             loading / skeleton placeholder
⛅ (offline)   PWA offline banner indicator
↻ (N)         queued offline writes indicator (N pending)
[ Button ]    clickable button
( ◦ )         toggle switch (off) / ( ●) on
›             "opens inspector / drills in"
```

> Convention: a focused/selected element is drawn with a **double border** `╔═╗` or a `▶` caret.
> Keyboard shortcuts mirror [UI.md §8](UI.md#8-interaction-details):
> `T`=today, `M/W/D/A/G`=Month/Week/Day/Agenda/Map (multi-month via the view switcher), `←/→`=navigate,
> `/`=search, `N`=new event.

---

## 1. App shell — desktop 3-pane

The base frame for every view: **Sidebar** (Accounts / Calendars / Categories) · **View canvas**
(toolbar + active view) · **Inspector**. Layout per [UI.md §2](UI.md#2-layout).

```text
┌─────────────────────┬──────────────────────────────────────────────────────┬───────────────────────┐
│ ◷ Unified Calendar  │  ┌────────────────────────────────────────────────┐  │  INSPECTOR            │
│ ───────────────────  │ │ [Month|Multi|Week|Day|Agenda|Map]  ‹ Jul 2026 › │ │  ───────────────────  │
│ ▾ ACCOUNTS      ⠿    │  │  [Today]   [🔍 search…  /]            [+ New]  │  │                       │
│   ⠿ ◉ Personal      │  └────────────────────────────────────────────────┘  │   (nothing selected)  │
│   ⠿ ○ Work          │  ┌────────────────────────────────────────────────┐  │                       │
│   ⠿ ○ Public        │  │                                                │  │   Select an event,    │
│   [+ Add account]   │  │                                                │  │   trip, or place to   │
│ ───────────────────  │ │           ACTIVE VIEW CANVAS                    │ │   see details,        │
│ ▾ CALENDARS         │  │   (Month / Multi-month / Week / Day /          │  │   travel time, and    │
│   ☑ ● Holidays      │  │    Agenda / Map — shared event stream)         │  │   prices here.        │
│   ☑ ● Birthdays     │  │                                                │  │                       │
│   ☑ ● Work          │  │                                                │  │                       │
│   ☐ ● Personal      │  │                                                │  │                       │
│ ───────────────────  │ │                                                │ │                       │
│ ▾ CATEGORIES        │  │                                                │  │                       │
│   ☑ Work            │  │                                                │  │                       │
│   ☐ Birthdays       │  │                                                │  │                       │
│   ☑ Travel          │  └────────────────────────────────────────────────┘  │                       │
│ ───────────────────  │                                                       │                       │
│ ⛅ offline · ↻ (0)   │                                                       │  [ ‹ collapse ]       │
└─────────────────────┴──────────────────────────────────────────────────────┴───────────────────────┘
```

**Interaction annotations**
1. **View switcher** `[Month|Multi|Week|Day|Agenda|Map]` — segmented control; switching is instant
   (re-projects the same in-memory stream, never re-queries). Keys `M/W/D/A/G`; Multi-month from the
   switcher. Source: already-loaded `GET /events` projection ([UI.md §3](UI.md#3-views)).
2. **Date nav** `‹ Jul 2026 ›` — prev/next period; keys `←/→`. Re-windows `GET /events?from=&to=`.
3. **[Today]** — jumps to today; key `T`.
4. **Search box** `[🔍 …]` — key `/`; queries all sources, renders results in Agenda (§5). Source:
   `GET /events?...` + client index over title/location/attendee.
5. **[+ New]** — drag-create alternative; key `N`. Opens a draft in the Inspector; write gated behind
   `calendar.write` (`POST /events`).
6. **Accounts list** — `◉/○` mark canonical-priority order; **drag handle `⠿` reorders accounts to set
   dedup priority** ([ARCHITECTURE.md §12](ARCHITECTURE.md#12-duplicate-detection--grouping)). Reorder
   persists via `PATCH /accounts/{id}` (`priority`). Data: `GET /accounts`.
7. **[+ Add account]** — opens the schema-driven connect flow (§8).
8. **Calendars list** `☑/☐ ●` — per-calendar show/hide + color swatch. Toggle: `PATCH /calendars/{id}`
   (`isVisible`). Data: `GET /calendars`.
9. **Categories list** `☑/☐` — hide whole classes (e.g. Birthdays) in one toggle. `PATCH /categories/{id}`.
   Data: `GET /categories`.
10. **Group disclosures** `▾/▸` collapse Accounts / Calendars / Categories sections.
11. **Inspector** — context panel; empty state shown. Collapsible via `[ ‹ collapse ]`
    (becomes a bottom sheet on mobile, §11).
12. **Status footer** `⛅ offline · ↻ (N)` — PWA offline + queued-writes indicator (§12), live from
    service worker + `GET /sync/status`.

---

## 2. Month view

Classic grid; **event chips colored by source calendar**, an **overflow `+N more`**, a collapsed
**`+N duplicates`** affordance, and a **free/busy shading toggle**. Per [UI.md §3](UI.md#3-views) +
[§7](UI.md#7-overlays-freebusy--prices).

```text
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [Month▼] ‹ July 2026 ›   [Today]   [🔍…]            [Free/Busy ( ●)]   [+ New]        │
├───────┬───────┬───────┬───────┬───────┬───────┬───────────────────────────────────── ┤
│  Mon  │  Tue  │  Wed  │  Thu  │  Fri  │  Sat  │  Sun                                  │
├───────┼───────┼───────┼───────┼───────┼───────┼─────────────────────────────────────┤
│  29   │  30   │  1    │  2    │  3    │  4    │  5                                    │
│       │       │       │█▌Offsit│       │░░░░░░│ ▓▓▓▓                                  │
│       │       │       │█▌Lunch │       │░░░░░░│ ▓▓▓▓                                  │
├───────┼───────┼───────┼───────┼───────┼───────┼─────────────────────────────────────┤
│  6    │  7    │  8    │  9    │  10   │  11   │  12                                   │
│█▌Stand│█▌1:1  │█▌Demo │█▌Ship │       │       │ █▌Brunch                              │
│█▌Holid│       │█▌Call │█▌Sync │       │       │                                       │
│+2 dup │       │+3 more│       │       │       │                                       │
├───────┼───────┼───────┼───────┼───────┼───────┼─────────────────────────────────────┤
│  13   │  14   │  15   │  16   │  17   │  18   │  19                                   │
│       │█▌Trip✈│┄┄┄┄┄┄│┄┄┄┄┄┄ │┄┄┄┄┄┄│       │                                       │
│       │       │ (draft Lisbon trip — dashed scenario layer)│                          │
└───────┴───────┴───────┴───────┴───────┴───────┴─────────────────────────────────────┘
```

**Interaction annotations**
1. **Event chip `█▌Title`** — colored by **source calendar** (color is never the only signal: icon +
   label accompany it, [UI.md §10](UI.md#10-accessibility-theming--offline)). Click › opens event in
   Inspector (§7). Source: `GET /events?from=&to=` (canonical only).
2. **`+N more`** — overflow when a day has more chips than fit; click expands a day popover / switches to
   Day view. Virtualized — only visible cells mount ([UI.md §9](UI.md#9-performance)).
3. **`+N dup` / `+N duplicates`** — collapsed duplicate group (e.g. a holiday in Holidays + Work). Click ›
   expands the duplicate group panel (§9). Data: `duplicateCount` on the event; members via
   `GET /events/{id}/duplicates`.
4. **Free/Busy toggle `( ●)`** — paints combined availability shading `▓/░` across visible calendars;
   open windows become obvious. Source: `GET /events?...&includeBusy=true` ([UI.md §7](UI.md#7-overlays-freebusy--prices)).
5. **Draft chips `┄┄┄`** — scenario/draft layer rendered dashed/distinct ([UI.md §4](UI.md#4-the-planning-model)).
   Data: `GET /trips` candidate items.
6. **Drag-create** — drag across empty grid to create; **drag-move/resize** existing chips (write gated).
   `POST /events` / `PATCH /events/{id}`.
7. **Day-number cell** — click a date number to drill into Day view for that date.
8. Date nav `←/→`, `[Today]`=`T`, search=`/`, new=`N`.

---

## 3. Multi-month view (the vacation planner)

A **3×4 year grid** with **cross-month range selection**, **flight cheapest-date price badges**,
**hotel nightly-rate overlay**, and **free/busy windows**. This is the vacation-planning surface
([UI.md §3](UI.md#3-views) + [§4](UI.md#4-the-planning-model) + [§7](UI.md#7-overlays-freebusy--prices)).

```text
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ [Multi-month▼] ‹ 2026 ›  [Today]   Overlays: [Free/Busy(●)] [Flights(●)] [Hotels(●)]       │
├──────────────────────────┬──────────────────────────┬──────────────────────────────────────┤
│  JAN                     │  FEB                     │  MAR                                 │
│  M T W T F S S           │  M T W T F S S           │  M T W T F S S                       │
│      1 2 3 4 5           │            1 2           │            1                         │
│  6 7 8 ...               │  2 3 4 5 6 7 8           │  2 3 4 5 6 7 8                       │
├──────────────────────────┼──────────────────────────┼──────────────────────────────────────┤
│  APR                     │  MAY                     │  JUN                                 │
│  ...                     │  ...                     │  ...                                 │
├──────────────────────────┼──────────────────────────┼──────────────────────────────────────┤
│  JUL                     │  AUG                     │  SEP                                 │
│  M T W T F S S           │  M T W T F S S           │  M T W T F S S                       │
│        1 2 3 4 5         │            1 2           │     1 2 3 4 5 6                      │
│  6 7 8 9 10 11 12        │  3 4 5 6 7 8 9           │  7 ...                               │
│ ░░░░ ╔════════════════╗  │ ╔══════════╗ ▓▓ 18 19    │                                     │
│      ║13 14 15 16 17 18║──┼─║1 2 3 4 5 ║  hotel€88 │   ← RANGE SELECT spans Jul13→Aug05   │
│ ✈€212║ 19 20 …        ║  │ ║ €74/nt    ║           │      (highlighted, cross-month)      │
│      ╚════════════════╝  │ ╚══════════╝             │                                     │
├──────────────────────────┴──────────────────────────┴──────────────────────────────────────┤
│  OCT          NOV          DEC      │  Selection: Jul 13 – Aug 05 (24 nights)               │
│  ...          ...          ...      │  ✈ cheapest dep Jul13 €212  ·  🏨 €74–88/nt  [Plan ›]  │
└─────────────────────────────────────┴───────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Range selection `╔══╗`** — click-drag across day cells, **across month boundaries**, to set the
   planning window. Drives prices, free/busy, and "find an open week" ([UI.md §4](UI.md#4-the-planning-model)).
   Selection state shown in the summary bar.
2. **Flight cheapest-date badge `✈€212`** — per-candidate-date price for a watched/queried route. Source:
   `GET /fares/overlay?...` (flights) ([UI.md §7](UI.md#7-overlays-freebusy--prices)).
3. **Hotel nightly-rate overlay `€74/nt` / `hotel€88`** — nightly price for destination + window. Source:
   `GET /fares/overlay?...` (stays).
4. **Free/busy windows `░/▓`** — shaded busy density makes open weeks pop. `GET /events?...&includeBusy=true`.
5. **Overlay toggles** `[Free/Busy][Flights][Hotels]` — non-destructive layers; toggle per planning
   session. Each maps to the overlay endpoints above.
6. **`[Plan ›]`** in summary — promotes the selection into a draft Trip (scenario layer) for flights +
   stays. `POST /trips` (candidate items), surfaced as dashed drafts in other views.
7. **Virtualized** — 12+ month cells mount only when visible ([UI.md §9](UI.md#9-performance)).
8. Year nav `←/→`, `[Today]`=`T`.

---

## 4. Week / Day view — travel-time gaps

Time grid showing a **travel-time gap chip** between events at different locations, a **"leave by"**
hint, a **"you can't make it"** conflict (gap < commute), and an **auto-inserted travel-buffer block**.
Per [UI.md §6](UI.md#6-travel-time--directions).

```text
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ [Week▼] ‹ Jul 6 – 12, 2026 ›  [Today]            mode: (🚗▾)  [Free/Busy(○)]   [+ New]    │
├──────┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────┬──────────────────┤
│ time │  Mon 6   │  Tue 7   │  Wed 8   │  Thu 9   │  Fri 10  │  Sat 11  │  Sun 12          │
├──────┼──────────┼──────────┼──────────┼──────────┼──────────┼──────────┼──────────────────┤
│ 09   │          │          │          │          │          │          │                  │
│ 10   │█▌Standup │          │          │          │          │          │                  │
│ 11   │ @Office  │          │          │          │          │          │                  │
│ 12   │          │          │          │          │          │          │                  │
│ 13   │ - - - - -│          │          │          │          │          │                  │
│ 14   │⚑ leave by│          │          │          │          │          │                  │
│      │  14:05   │          │          │          │          │          │                  │
│      │┈🚗 32 min┈ (travel-time gap chip · @Office → @Client) │          │                  │
│ 15   │▒▒ Travel │          │          │          │          │          │                  │
│      │▒▒ buffer │          │          │          │          │          │                  │
│ 16   │█▌Client  │          │          │          │          │          │                  │
│ 17   │ mtg      │          │          │          │          │          │                  │
├──────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────┴──────────────────┤
│  ── Day 9 detail (conflict state) ───────────────────────────────────────────────────────  │
│ 13 │█▌Lunch @Soho ………ends 13:30                                                            │
│    │⚠ 🚆 transit 48 min — YOU CAN'T MAKE IT (gap 15 min < 48 min)   [Move ›][Buffer][Mode▾]│
│ 14 │█▌Board call @Canary Wharf  starts 13:45                                                │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Commute chip `┈🚗 32 min┈`** — drawn in the gap between two consecutive events at different
   geocoded places, for the chosen **mode**. Click › opens route in Inspector (§7). Source:
   `POST /route` (mode + `arriveBy` = next event start) or precomputed `GET /events/{id}/commute`.
2. **"Leave by" hint `⚑ 14:05`** — anchored to the earlier event; `leaveByUtc` from the route response.
3. **Travel-buffer block `▒▒`** — optional auto-inserted event so the time is visibly blocked
   ([UI.md §6](UI.md#6-travel-time--directions)). User setting; can be inserted on demand via `[Buffer]`.
4. **Conflict state `⚠ … YOU CAN'T MAKE IT`** — shown when `feasible:false` (gap < commute) from
   `POST /route`. Inline fixes: `[Move ›]` (suggested reschedule), `[Buffer]`, `[Mode▾]` (re-route by a
   faster mode).
5. **Mode picker `(🚗▾)`** — drive/transit/walk/bike; per-user default, re-queries `POST /route` and
   re-evaluates feasibility. Transit respects schedules via arrival time.
6. **Event blocks `█▌`** — click › Inspector; drag-move/resize recomputes the affected `ROUTE_LEG`s only
   (incremental projection, [UI.md §9](UI.md#9-performance)). `PATCH /events/{id}`.
7. View `W`/`D`, date nav `←/→`, `[Today]`=`T`, `[+ New]`=`N`.

---

## 5. Agenda view (dense list + search results surface)

Dense chronological list; **also the search-results surface** ([UI.md §3](UI.md#3-views) +
[§8](UI.md#8-interaction-details)). Great on mobile.

```text
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [Agenda▼]  [🔍 client mtg                              /]   12 results   [Clear ✕]    │
├────────────────────────────────────────────────────────────────────────────────────┤
│  THU · JUL 2                                                                         │
│  08:00–17:00  █▌ Team offsite          @Berlin HQ        Work        ›               │
│  12:30–13:30  █▌ Lunch w/ Sam          @Soho                          ›               │
│ ─────────────────────────────────────────────────────────────────────────────────── │
│  MON · JUL 6                                                                         │
│  10:00–10:30  █▌ Standup               @Office           Work        ›               │
│  16:00–17:00  █▌ Client mtg            @Canary Wharf     Work    🚗32m›               │
│               +2 duplicates ▸                                                         │
│ ─────────────────────────────────────────────────────────────────────────────────── │
│  TUE · JUL 14                                                                        │
│  ┄┄┄┄┄┄┄┄┄┄  ┄┄ Lisbon trip (draft)   @Lisbon          Travel  (scenario) ›          │
│ ─────────────────────────────────────────────────────────────────────────────────── │
│  ◐ loading earlier… (virtualized — scroll to load more)                              │
└────────────────────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Row `›`** — click › opens the event/trip in the Inspector (§7); "jump in context" switches to the
   day in Week/Day. Source: `GET /events?from=&to=`.
2. **Search box `[🔍…]`** — key `/`. Queries title/location/attendee across **all sources**; this list
   becomes the results surface. Clearing `[✕]` restores the chronological agenda.
3. **`+N duplicates ▸`** — inline expand to the duplicate group (§9). `GET /events/{id}/duplicates`.
4. **Commute tag `🚗32m`** — compact travel-time to the next event (`GET /events/{id}/commute`).
5. **Draft rows `┄┄`** — scenario/draft layer, labeled `(scenario)`. `GET /trips` candidates.
6. **Virtualized list `◐`** — only visible rows mount; scroll loads more via cursor
   (`?cursor=`, [UI.md §9](UI.md#9-performance)).
7. View `A`.

---

## 6. Map view

World map with **clustered pins**, a **date-range scrubber**, a **drawn trip route between legs**, a
**selected-pin popup**, and **click-to-place**. Per [UI.md §5](UI.md#5-map-view) +
[ARCHITECTURE.md §7](ARCHITECTURE.md#7-map-geocoding--routing). Rendered by MapLibre GL.

```text
┌────────────────────────────────────────────────────────────────────────────────────┐
│ [Map▼]  ‹ Jul 2026 ›   [Today]   basemap:(OSM ▾)   [Filter to region ▢]   [+ New]    │
├────────────────────────────────────────────────────────────────────────────────────┤
│                                  ◎12                                                  │
│            ●─────────⤢ (flight arc)──────────● Lisbon                                 │
│         London                              ╔══════════════════════╗                 │
│            │                                ║ ● Lisbon — Jul 14–21  ║ ‹popup›         │
│            ⟶ (drive leg)                    ║ Hotel + 3 events      ║                 │
│            ● Brighton                       ║ 🚆 from airport 22 min ║                 │
│                                  ◎ 5        ║ [Open in Agenda ›]    ║                 │
│                       ◎ 8                   ║ [Watch fare][Share]   ║                 │
│                                             ╚══════════════════════╝                 │
│                                                                       + (click here   │
│                                                                          to place a   │
│                                                                          new pin)     │
├────────────────────────────────────────────────────────────────────────────────────┤
│  DATE SCRUBBER  Jan ├──────────●═════════●──────────────┤ Dec     [▶ play timeline]  │
│                          Jul 13 ▲       ▲ Aug 05  (drag to reveal pins over time)     │
└────────────────────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Pins `●` / clusters `◎N`** — every event/trip with a resolved `Place`; clustered at low zoom.
   Source: `GET /map/events?from=&to=&region=`. Click a cluster to zoom/expand.
2. **Date-range scrubber** — synced to the current selection; drag handles to watch pins appear/disappear
   over time; `[▶ play]` animates. Re-queries `GET /map/events` with the scrubbed window.
3. **Trip routes `⤢`/`⟶`** — flight arcs + drive lines between consecutive legs, drawn from
   `geo.route` geometry (encoded polyline on `POST /route`, cached per `ROUTE_LEG`).
4. **Selected-pin popup `╔══╗`** — title, window, item count, airport commute; actions `[Open in Agenda]`,
   `[Watch fare]` (`POST /fares/watches`), `[Share]` (`POST /shares`).
5. **Click-to-place `+`** — click an empty map point to drop a pin → reverse-geocoded to an address and
   set as the event/draft location. Source: `POST /geocode` (reverse) ([UI.md §5](UI.md#5-map-view)).
6. **`[Filter to region ▢]`** — draw/zoom a region to filter the **other** views to events there
   (geographic filtering); passes `region=` to `GET /events`/`/map/events`.
7. **Basemap `(OSM ▾)`** — chooses the `geo.tiles` plugin (OSM default; Google/Mapbox if installed).
   Source: `GET /tiles/style`. Degrades to cached tiles offline ([UI.md §10](UI.md#10-accessibility-theming--offline)).
8. View `G`, date nav `←/→`, `[Today]`=`T`.

---

## 7. Inspector panel — event detail

Selected event detail: **title/time/calendar/category/color**, the **geocoded Place + mini-map**, the
**commute / leave-by** info, **watch fare**, and **share**. Per [UI.md §2](UI.md#2-layout) +
[§8](UI.md#8-interaction-details).

```text
┌───────────────────────────────────┐
│ INSPECTOR                    [✕]   │
│ ─────────────────────────────────  │
│ Client mtg                         │  ← title (editable)
│ Mon Jul 6 · 16:00–17:00            │
│                                    │
│ Calendar  ● Work            (▾)    │  ← source calendar + color swatch
│ Category  Work              (▾)    │
│ Color     ●●●●●● ● ●●●  [custom…]   │
│ ─────────────────────────────────  │
│ PLACE                              │
│  📍 Canary Wharf, London E14       │  ← geocoded Place
│  ┌─────────────────────────────┐   │
│  │  · · ● · ·   (mini-map)      │   │  ← MapLibre mini-map, pin on Place
│  │  · · · · ·                   │   │
│  └─────────────────────────────┘   │
│  [Set location…] [Pick on map ›]   │
│ ─────────────────────────────────  │
│ TRAVEL                             │
│  Mode (🚗 drive ▾)                 │
│  🚗 32 min from Office             │  ← commute to/from adjacent event
│  ⚑ Leave by 15:28                  │
│  [Insert travel buffer]            │
│ ─────────────────────────────────  │
│ ACTIONS                            │
│  [👁 Watch fare]  [🔗 Share]        │
│  [Duplicate]      [Delete]         │
└───────────────────────────────────┘
```

**Interaction annotations**
1. **Title / time** — editable; saves via `PATCH /events/{id}` (gated by `calendar.write`; queues offline).
2. **Calendar / Category / Color** — change source calendar mapping, category, or override color
   ([UI.md §8](UI.md#8-interaction-details)). `PATCH /events/{id}` / `PATCH /calendars/{id}` for color.
3. **Place + mini-map** — geocoded location with a MapLibre mini-map. `[Set location…]` autocompletes via
   `GET /places/search?q=`; `[Pick on map ›]` opens click-to-place (§6). `POST /geocode` resolves.
4. **Travel block** — mode picker + computed commute + `⚑ leave by` from `POST /route` /
   `GET /events/{id}/commute`. `[Insert travel buffer]` adds the buffer event (§4).
5. **`[Watch fare]`** — start a `FareWatch` for this place/route. `POST /fares/watches`.
6. **`[Share]`** — publish as tokenized link (FullDetails / FreeBusy scope). `POST /shares` →
   `{ token, url }`.
7. **`[Duplicate]`** — duplicate affordance: clones the event as a new draft. `[Delete]` →
   `DELETE /events/{id}` (reversible suppression, never hard-delete per dedup model).
8. **`[✕]`** closes the Inspector (collapses to bottom sheet on mobile, §11).

---

## 8. Add-account / connect flow (schema-driven)

The **Add-account form is auto-generated from the plugin's config JSON Schema** + auth scheme; presets
seed known providers. Per [PLUGINS.md §9](PLUGINS.md#9-schema-driven-configuration-ui) +
[API.md Accounts](API.md#accounts--connect-flow).

```text
┌──────────────────────────── Add account ─────────────────────────────┐
│  ‹ 1. Choose provider ›                                         [✕]   │
│  Presets:  [ iCloud ] [ Fastmail ] [ Nextcloud ] [ Proton ] [ TripIt ]│
│            [ Google ] [ Microsoft ] [ ICS feed ] [ Custom CalDAV ]    │
│  ───────────────────────────────────────────────────────────────────  │
│  ‹ 2. Connect ›   (form rendered from plugin.config JSON Schema)      │
│                                                                       │
│  ── CalDAV (app-password scheme) ─ e.g. iCloud / Fastmail ──────────  │
│   Server URL *   [ https://caldav.icloud.com________________ ]        │
│   Username   *   [ you@icloud.com__________________________ ]         │
│   App password*  [ ••••••••••••••••  ] (stored in vault, never synced)│
│                                       [ Test connection ]  [Connect]  │
│                                                                       │
│  ── OAuth (oauth2-pkce scheme) ─ e.g. Google / Microsoft ───────────  │
│   ( ◉ )  [  Sign in with Google  ›  ]   (host runs PKCE dance)        │
│                                                                       │
│  ── ICS feed (none) ─ e.g. Proton link / TripIt .ics ──────────────  │
│   Feed URL  *    [ https://…/calendar.ics_________________ ]          │
│ ─────────────────────────────────────────────────────────────────── │
│  ⓘ Fields, requireds, defaults & the connect button come from the     │
│    selected plugin's JSON Schema + auth.scheme — no hardcoded UI.     │
└───────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Presets** — seed `pluginId` + default `config` (e.g. iCloud → CalDAV plugin + `caldav.icloud.com`).
   Source: `GET /plugins` (each plugin's `configSchema` + `auth`).
2. **Schema-driven fields** — every input (label, required `*`, default, type/validation) is generated
   from `plugin.config` JSON Schema; **no per-provider UI code** ([PLUGINS.md §9](PLUGINS.md#9-schema-driven-configuration-ui)).
3. **Auth scheme drives the button** —
   `app-password`/`basic` → URL+user+secret fields + `[Connect]`;
   `oauth2-pkce` → `[Sign in with…]`;
   `none` → just config (ICS URL).
4. **App-password / API-key (single step)** — `POST /accounts {pluginId, config, secret}` → `201` with
   calendars; the **secret goes straight to the vault**, never returned to the UI.
5. **OAuth (PKCE)** — `POST /accounts {pluginId, config}` → `{ authChallenge: { redirectUrl } }`; UI opens
   it; host handles `GET /accounts/oauth/callback` then `302` back as `status:"connected"`.
6. **`[Test connection]`** — dry-run validation before commit.
7. On success the new account appears in the Sidebar (§1) and its calendars in the Calendars list.

---

## 9. Duplicate group expanded

A duplicate group with **merge / split / never-merge** controls and **"why these matched"**. Per
[UI.md §8](UI.md#8-interaction-details) + [ARCHITECTURE.md §12](ARCHITECTURE.md#12-duplicate-detection--grouping).

```text
┌─────────────────── Duplicate group — "Independence Day" · Jul 4 ───────────────────┐
│  WHY THESE MATCHED                                                            [✕]   │
│   signature = hash(title · startDate · allDay)                                     │
│   matched on:  title "independence day"  ·  2026-07-04  ·  all-day ✓               │
│ ──────────────────────────────────────────────────────────────────────────────────  │
│  MEMBERS (3)                          canonical chosen by account priority          │
│   ◉ ● Holidays (Personal)   ← canonical (shown)        priority 1                   │
│   ○ ● US Holidays (Work)    (suppressed)               priority 2                   │
│   ○ ● Public Holidays       (suppressed)               priority 3                   │
│ ──────────────────────────────────────────────────────────────────────────────────  │
│  [ Set canonical ]   [ Merge all ]   [ Split (never-merge) ]                        │
│  ⓘ Reversible — suppression is a view-layer decision; nothing is deleted.           │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **"Why these matched"** — shows the dedup signature + the fields that matched (title/date/all-day).
   Source: `GET /events/{id}/duplicates` (returns members + match reason).
2. **Member radios `◉/○`** — pick which member is **canonical** (the one shown). Reorder hint: priority
   comes from account order (Sidebar §1, drag `⠿`). `POST /duplicates/{groupId}/canonical {eventId}`.
3. **`[Merge all]`** — force-merge the listed events. `POST /duplicates/merge {eventIds[]}`.
4. **`[Split (never-merge)]`** — never-merge override; the events show separately henceforth.
   `POST /duplicates/split {eventIds[]}`.
5. **Reversible** — all actions are view-layer + undoable; nothing is deleted
   ([ARCHITECTURE.md §12](ARCHITECTURE.md#12-duplicate-detection--grouping)). Overrides sync as user metadata.

---

## 10. Plugins / Settings

Installed plugins, their **capabilities**, **enable/disable**, and **install-from-URL**. Per
[API.md Plugins](API.md#plugins--capabilities) + [PLUGINS.md](PLUGINS.md).

```text
┌──────────────────────────────── Settings · Plugins ──────────────────────────────────┐
│  [Accounts] [Plugins] [Theme] [Sync] [Sharing]                                        │
│ ─────────────────────────────────────────────────────────────────────────────────────  │
│  INSTALLED                                          [ Install from URL… ] [ From file ]│
│ ─────────────────────────────────────────────────────────────────────────────────────  │
│  ● Google Calendar        v3.4.0  in-box   calendar.read              connected ( ●)   │
│       caps: calendar.read · calendar.write(soon)                       [Configure ▾]   │
│  ● CalDAV                 v2.1.0  in-box   calendar.read               connected ( ●)   │
│  ● ICS feeds              v1.0.2  in-box   calendar.read               connected ( ●)   │
│  ● MapLibre / OSM tiles   v1.1.0  in-box   geo.tiles                   active    ( ●)   │
│  ● OSRM routing           v0.9.0  signed   geo.route                   active    ( ●)   │
│  ● Nominatim (self-host)  v0.8.1  signed   geo.geocode · geo.places    active    ( ●)   │
│  ● Duffel fares           v1.2.0  signed   flight.price · stay.price   disabled  ( ◦)   │
│       network allow: api.duffel.com   ·   auth: oauth2-cc   ·   out-of-process: no     │
│ ─────────────────────────────────────────────────────────────────────────────────────  │
│  ⓘ Capability → provider mapping drives "what the app can do right now" (GET /capabilities).│
└────────────────────────────────────────────────────────────────────────────────────────┘
```

**Interaction annotations**
1. **Plugin rows** — id/name, version, **trust tier** (in-box/signed/community/local-dev), declared
   **capabilities**, status. Source: `GET /plugins` (+ `GET /capabilities` for the mapping note).
2. **Enable/disable toggle `( ●)/( ◦)`** — turns a plugin on/off; routing/aggregator re-evaluates
   available capabilities live.
3. **`[Configure ▾]`** — opens the **schema-driven** config form (same renderer as §8) from the plugin's
   `configSchema`. `GET /plugins/{id}`.
4. **`[Install from URL…]` / `[From file]`** — installs a bundle; host validates SDK version, signature,
   and permissions before load. `POST /plugins {source, signature?}`.
5. **Uninstall** (in `[Configure ▾]`) — unloads the plugin's ALC. `DELETE /plugins/{id}`.
6. **Network/auth/out-of-process** metadata reflects the manifest sandboxing
   ([PLUGINS.md §8](PLUGINS.md#8-security--sandboxing)).

---

## 11. Responsive / mobile (bottom sheets)

The same shell collapses to **bottom sheets** with the **canvas primary**. Sidebar and Inspector become
sheets; bottom tab bar switches views. Shown: **Month** + **Map** at phone width
([UI.md §2](UI.md#2-layout) + [§10](UI.md#10-accessibility-theming--offline)).

```text
  MONTH (phone)                          MAP (phone)
┌───────────────────────┐             ┌───────────────────────┐
│ ☰  Jul 2026   🔍  [+] │             │ ☰  Jul 2026   🔍  [+] │
│ ⛅ offline · ↻ (2)     │             │           ◎12         │
├───────────────────────┤             │      ●──⤢──● Lisbon   │
│ M  T  W  T  F  S  S    │             │   London              │
│ 29 30 1  2  3  4  5    │             │      ⟶ ● Brighton     │
│       █▌    ░░ ▓▓      │             │    ◎8        ◎5       │
│ 6  7  8  9 10 11 12    │             │                       │
│ █▌ █▌ █▌ █▌      █▌     │             │   + tap to place pin  │
│ +2 +3more              │             │                       │
│ 13 14 15 16 17 18 19   │             ├───────────────────────┤
│    ┄┄ ┄┄ ┄┄(draft)     │             │ SCRUBBER              │
├───────────────────────┤             │ Jan ├──●══●──┤ Dec ▶   │
│ ╭───────────────────╮  │  ← sheet    ├───────────────────────┤
│ │ ▁ Standup 10:00   │  │   peeking   │ ╭───────────────────╮ │
│ │   @Office  Work › │  │   (drag up  │ │ ▁ Lisbon Jul14–21 │ │
│ ╰───────────────────╯  │   to expand │ │  🚆22m [Watch][↗] │ │
├───────────────────────┤   Inspector)│ ╰───────────────────╯ │
│ [Mon][Wk][Day][Ag][Map]│            │ [Mon][Wk][Day][Ag][Map]│
└───────────────────────┘             └───────────────────────┘
```

**Interaction annotations**
1. **`☰` hamburger** — opens the **Sidebar as a bottom/side sheet** (Accounts/Calendars/Categories).
2. **Inspector bottom sheet `╭──╮`** — selecting an event raises a peeking sheet; drag up to expand to the
   full Inspector (§7). Same data/actions as desktop.
3. **Bottom tab bar `[Mon][Wk][Day][Ag][Map]`** — view switcher; canvas stays primary
   ([UI.md §2](UI.md#2-layout)).
4. **Map sheet** — pin popup also rises as a bottom sheet; **tap-to-place** replaces click-to-place.
5. **Same endpoints** as desktop (`GET /events`, `GET /map/events`); offline + queued-writes indicator
   persists in the header.
6. Touch gestures replace drag-create where needed; all keyboard shortcuts remain for external keyboards.

---

## 12. Canvas states — empty / loading / offline / error

States for the main canvas, including the **PWA offline banner** + **queued-writes indicator**. Per
[UI.md §9](UI.md#9-performance) + [§10](UI.md#10-accessibility-theming--offline) + SSE refresh
([API.md Sync](API.md#sync)).

```text
 (a) LOADING (skeleton — never a blocking spinner)        (b) EMPTY (no accounts yet)
┌────────────────────────────────────┐                  ┌────────────────────────────────────┐
│ ◐ ◐ ◐ ◐ ◐ ◐ ◐                       │                  │            ◷                        │
│ ◐◐  ◐◐  ◐◐  ◐◐  ◐◐                   │                  │   No calendars connected yet.       │
│ ◐◐  ◐◐  ◐◐  ◐◐  ◐◐                   │                  │   Connect an account to see your    │
│  (cells mount as projection loads   │                  │   life across all your calendars.   │
│   from IndexedDB cache → instant)   │                  │        [ + Add account ]            │
└────────────────────────────────────┘                  └────────────────────────────────────┘

 (c) OFFLINE (PWA — cached projection + queued writes)    (d) ERROR (sync/provider failure)
┌────────────────────────────────────┐                  ┌────────────────────────────────────┐
│ ⛅ You're offline — showing the last │                  │ ⚠ Couldn't sync "Work" (Google).    │
│    cached view. Edits will sync      │                  │   Showing last-known data (stale).  │
│    when you're back.       ↻ (2) ▾  │                  │   [ Retry ]   [ View details ]      │
├────────────────────────────────────┤                  ├────────────────────────────────────┤
│  (Month grid renders from cache;     │                  │  (grid still renders cached events; │
│   map shows cached tiles only)       │                  │   only the failed source is flagged)│
└────────────────────────────────────┘                  └────────────────────────────────────┘
```

**Interaction annotations**
1. **Loading skeleton `◐`** — virtualized cells mount as the projection streams from the **IndexedDB
   cache** (instant cold start); no blocking spinner between views ([UI.md §9](UI.md#9-performance)).
   Source: cached `GET /events` projection + live `GET /sync/stream` (SSE) deltas.
2. **Empty state** — no accounts connected → CTA `[+ Add account]` (opens §8). After first connect, the
   canvas fills from `GET /events`.
3. **Offline banner `⛅`** — service-worker-driven; the app shell + last projection are cached, map
   degrades to cached tiles ([UI.md §10](UI.md#10-accessibility-theming--offline)).
4. **Queued-writes indicator `↻ (N) ▾`** — count of offline edits queued to replay when back online (gated
   by `calendar.write`). Expand `▾` to review/cancel queued ops. Drains via `POST/PATCH/DELETE /events`
   on reconnect.
5. **Error state `⚠`** — a single failed source is flagged; the rest of the grid still renders cached
   data (graceful degradation, `stale` flag). `[Retry]` → `POST /accounts/{id}/sync`; `[View details]` →
   `GET /sync/status` (`lastError`).
6. **Live refresh** — when back online, `GET /sync/stream` pushes `eventsChanged{range}` and the UI
   re-projects only affected cells/legs (incremental, not a full re-render).
```
