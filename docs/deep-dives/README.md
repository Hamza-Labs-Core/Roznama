# Plugin deep dives

End-to-end engineering specs for each first-party plugin: the capability it implements, auth, the
protocol/flow, normalization to the domain model, .NET notes, provider quirks, a manifest, and a
failure-modes/tests checklist. Facts are grounded in research current as of **June 2026** (see each
doc's Sources, and the survey in [PLUGIN-RESEARCH.md](../PLUGIN-RESEARCH.md)).

All plugins plug into the same [capability system](../PLUGINS.md); the core never references a provider
by name.

## Calendars (`calendar.read` / `calendar.write`)

| Doc | Plugin(s) | Kind | Verdict |
| --- | --- | --- | --- |
| [ics-plugin.md](ics-plugin.md) | ICS/iCalendar feeds — **also carries Proton & TripIt** | light assembly | 🟢 (Proton/TripIt 🟡 workaround) |
| [google-calendar-plugin.md](google-calendar-plugin.md) | Google Calendar | assembly (Google.Apis) | 🟢 |
| [microsoft-graph-plugin.md](microsoft-graph-plugin.md) | Outlook / Microsoft 365 / work | assembly (Graph + MSAL) | 🟢 |
| [caldav-plugin.md](caldav-plugin.md) | iCloud · Fastmail · Nextcloud (CalDAV) | assembly | 🟢 |

## Maps & geo

| Doc | Capability | Plugin(s) | Verdict |
| --- | --- | --- | --- |
| [geo-tiles-plugin.md](geo-tiles-plugin.md) | `geo.tiles` | MapLibre/OpenMapTiles/PMTiles · MapTiler · Mapbox · Google | 🟢 self-host |
| [geo-geocoding-places-plugin.md](geo-geocoding-places-plugin.md) | `geo.geocode` · `geo.places` | Nominatim · Photon · Google/Mapbox/LocationIQ | 🟡 self-host |
| [geo-routing-plugin.md](geo-routing-plugin.md) | `geo.route` | OSRM · Valhalla · Google Routes v2 · Mapbox | 🟢 self-host / 🟡 transit |

## Travel

| Doc | Capability | Plugin(s) | Verdict |
| --- | --- | --- | --- |
| [travel-fares-plugin.md](travel-fares-plugin.md) | `flight.price` · `stay.price` | Duffel (Flights + Stays) · Kiwi | 🟡 (⚠️ not Amadeus) |
| (itinerary import) | `itinerary.import` | TripIt → via [ics-plugin.md](ics-plugin.md) | 🟡 feed only |

## Notable findings worth knowing before building

- **Proton & TripIt have no usable API** — both ride the **ICS plugin** (Proton share-link; TripIt
  calendar feed). No new auth work.
- **Amadeus Self-Service is decommissioned 2026-07-17** — fares lead with **Duffel** + **Kiwi**.
- **Google Directions API is now Legacy** (since 2025-03-01) — routing uses **Routes API v2
  `computeRoutes`**.
- **Microsoft `/me/events/delta` is beta-only** — sync uses **`/me/calendarView/delta`** on v1.0;
  work tenants need admin consent (Microsoft-managed policy from late 2025).
- **Nominatim's public endpoint forbids no-code/LLM-generated generic geocoding** and caps at 1 req/s —
  **self-host** (or a commercial geocoder); never ship against the public host.
- **Duffel's official .NET SDK is unmaintained** — prefer the **declarative connector** over a hard SDK
  dependency.
- **MapLibre GL JS is BSD-3** but **Mapbox GL JS v2+ is proprietary** — a "Mapbox" plugin means Mapbox
  tiles under a token, rendered by MapLibre.
