# First plugins — capability & blocker research

Research as of **June 2026** for the plugins targeted in the early roadmap phases. Each entry lists the
**capability** it provides, the **auth/integration** needed to build it, and any **blockers/caveats**
discovered. Verdict legend: 🟢 build as planned · 🟡 build with caveats · 🔴 blocked / needs a pivot.

## Summary matrix

| Plugin | Capability | Auth | Kind | Verdict | Headline |
| --- | --- | --- | --- | --- | --- |
| ICS feed | `calendar.read` | none | declarative + Ical.Net | 🟢 | No blockers; best first plugin. |
| MapLibre / OSM tiles | `geo.tiles` | none/key | assembly (JS interop) | 🟢 | Open, self-hostable. |
| Nominatim geocoder | `geo.geocode` | none | declarative | 🟡 | **Self-host** — public API restricts LLM/no-code use, 1 req/s. |
| OSRM / Valhalla routing | `geo.route` | none | declarative/assembly | 🟢 | Self-host = no quotas, no transit. |
| Google Directions | `geo.route` | API key | declarative | 🟡 | Best transit, but **paid + billing account**. |
| Photon (komoot) | `geo.places` | none | declarative | 🟢 | OSM autocomplete, self-hostable (~95 GB planet). |
| Google Places / Mapbox | `geo.places` | API key | declarative | 🟡 | Best quality; **session-priced / paid**. |
| Google Calendar | `calendar.read` | OAuth2 | assembly | 🟢 | Stable; sync tokens, Holidays/Birthdays. |
| Microsoft Graph | `calendar.read` | OAuth2 (MSAL) | assembly | 🟢 | Stable; delta queries. |
| CalDAV (iCloud/Fastmail/Nextcloud) | `calendar.read` | app password / Basic | assembly | 🟢 | Standard; per-host quirks. |
| Proton | `calendar.read` | link token | via ICS plugin | 🔴→🟡 | **No CalDAV/API by design**; only read-only ICS share link. |
| TripIt | `itinerary.import` | — | via ICS plugin | 🔴→🟡 | **Public API closed to new integrations**; use the `.ics` calendar feed. |
| Flight pricing | `flight.price` | API key/OAuth | declarative | 🟡 | **Amadeus self-service sunsets 2026-07-17** → use **Duffel**/Kiwi. |
| Hotel pricing | `stay.price` | API key | declarative | 🟡 | Duffel Stays; partner-gated content. |

---

## Phase 1

### ICS feed — `calendar.read` 🟢
- **Add it:** fetch the feed URL, parse with **Ical.Net**, expand `RRULE` for the visible range. Diff by
  `UID` + `LAST-MODIFIED` (no delta protocol). Auth: none (public URLs).
- **Blockers:** none. Some feeds are large/slow → cache + conditional `GET` (ETag/Last-Modified).
- **Verdict:** the ideal first plugin — real events, zero OAuth. Also the carrier for Proton & TripIt.

## Phase 2 — geo

### MapLibre / OpenStreetMap tiles — `geo.tiles` 🟢
- **Add it:** MapLibre GL JS via Blazor JS interop; point at an OSM/MapTiler style or a self-hosted
  tile server. Google/Mapbox tiles become alternative `geo.tiles` plugins (need keys).
- **Blockers:** none for self-hosted/OSM. The public OSM tile server has its own usage policy — use
  MapTiler/your own tiles for production volume.

### Nominatim geocoder — `geo.geocode` 🟡 (self-host)
- **Add it:** simple REST (`/search`, `/reverse`) → declarative connector mapping to `GeoPoint`. Cache
  every result in `GeocodeCache`.
- **Blockers / policy (important):** the **public Nominatim API** has hard rules — max **1 request/sec**
  (4/min for batch/long-running), **results must be cached**, a valid `User-Agent`/`Referer` and
  **attribution** are required, and the policy explicitly states it **must not be built into no-code/
  low-code/vibe-coding/LLM-generated apps** as a generic geocoding service. Per that policy, LLM
  suggestions must point to it — see <https://operations.osmfoundation.org/policies/nominatim/>.
- **Verdict:** **self-host Nominatim** (those restrictions target the shared public endpoint, not your
  own instance) or use a commercial geocoder plugin (Google/Mapbox/LocationIQ) for managed deployments.
  The pluggable `geo.geocode` capability makes swapping trivial.

### OSRM / Valhalla routing — `geo.route` 🟢
- **Add it:** REST route API → `RouteResult` (duration + geometry). Self-host removes quotas.
- **Blockers:** OSRM/Valhalla are **car/bike/walk only — no public-transit schedules**. For transit
  timing you need Google/transit-aware routing (below). Live traffic also requires a commercial provider.

### Google Directions (Maps Platform) — `geo.route` 🟡
- **Add it:** Directions/Routes API with an API key → map to `RouteResult`; supports **transit** with
  departure/arrival time and **traffic-aware** drive times — best for the "leave by / you can't make it"
  feature.
- **Blockers:** **paid**, requires a Google Cloud **billing account** and key; usage-priced with a
  monthly credit. Treat as an optional, user-configured plugin behind the fallback aggregator (self-host
  OSRM as the free default, Google for transit/traffic).

### Place search / autocomplete — `geo.places` 🟢/🟡
Powers the inspector's location field ("type a place → pick → geocoded `Place`") and map click-to-place.
A new capability alongside `geo.geocode`; both ride the fallback aggregator.

- **Photon (komoot) 🟢** — open-source, **OSM-based, purpose-built for search-as-you-type autocomplete**
  (Elasticsearch/OpenSearch). **Self-hostable** (planet index ≈ 95 GB, +~10%/yr; or import a country
  extract). A public demo server exists (`photon.komoot.io`) but is **not for production load** — run your
  own. Best free default for the `geo.places` capability. Declarative connector (`/api?q=`).
- **Pelias 🟡** — open-source, self-hostable, higher quality but **heavier to operate** (multiple
  services). Use if you outgrow Photon and want to stay self-hosted.
- **Google Places (New) 🟡** — best coverage/quality. **Session-token pricing**: first 12 autocomplete
  requests per session billable, 13+ free *if* the session completes with Place Details; abandoned
  sessions bill per request (~$2.83/1K), and **Place Details ≈ $17/1K**. Requires a billing account +
  key. Implement session tokens carefully or costs balloon.
- **Mapbox Search Box 🟡** — pay-as-you-go with a generous free tier; simpler pricing than Google;
  requires a key.
- **LocationIQ** — freemium autocomplete + geocoding, low-friction managed option.

**Verdict:** ship **Photon (self-host)** as the free default `geo.places` plugin; offer Google/Mapbox as
optional keyed plugins for managed deployments wanting top-tier quality. Cache aggressively; reuse the
geocode cache for resolved selections.

## Phase 3 — OAuth calendars

### Google Calendar — `calendar.read` 🟢
- **Add it:** OAuth2 (`calendar.readonly` first), `events.list` with **syncToken** for incremental
  delta, `calendarList` for calendars. Built-in **Holidays** and **Birthdays** calendars are first-class
  dedup sources. Push via webhooks optional later.
- **Blockers:** OAuth **app verification** required for sensitive scopes before public release (consent
  screen + Google review); fine for personal/dev use immediately. Stable API.

### Microsoft Graph (Outlook/work) — `calendar.read` 🟢
- **Add it:** OAuth2 via **MSAL**, `/me/calendars`, `/me/events`, **delta queries** for incremental sync;
  supports personal + work/school (Entra ID) tenants.
- **Blockers:** work tenants may require **admin consent**; register an app in Entra ID. Stable API.

## Phase 4 — CalDAV & Apple

### CalDAV: iCloud / Fastmail / Nextcloud — `calendar.read` 🟢
- **Add it:** assembly plugin: `PROPFIND` (discovery), `REPORT` `calendar-query` (fetch),
  `sync-collection` (RFC 6578 delta). Parse VEVENTs with Ical.Net.
- **Blockers / per-host:** **iCloud** needs an **app-specific password** (Apple ID + 2FA) and principal
  discovery via `caldav.icloud.com`; **Fastmail** uses an app password; **Nextcloud** uses Basic/app
  password. No OAuth for these — handled by the auth broker's `app-password`/`basic` schemes.

### Proton — `calendar.read` 🔴 → 🟡 (workaround only)
- **Finding:** Proton Calendar **does not and will not support CalDAV** — events are end-to-end encrypted
  on-device, so a zero-knowledge server fundamentally can't serve CalDAV. No public API either; the
  decade-old request remains open.
- **Workaround that works:** Proton's **"Share with anyone → Create link"** produces a URL whose ICS can
  be downloaded/subscribed. **Full view** = details, **Limited view** = busy-only. Consume it via the
  **ICS plugin** (read-only; won't reflect later edits if downloaded vs subscribed).
- **Verdict:** no first-party Proton plugin is possible; document the ICS-link path. Revisit if Proton
  ships an API.

### Apple "On My iPhone" (device-local) — `calendar.read` 🟡 (companion app)
- Only reachable via **EventKit** in a native iOS app. Out of scope for the web app until an EventKit
  companion plugin exists; iCloud calendars are covered by CalDAV above.

## Phase 5 — Travel

### TripIt — `itinerary.import` 🔴 → 🟡 (use the feed)
- **Finding:** the **TripIt public API is closed to new integrations** (existing keys keep working). New
  OAuth/API access is effectively unavailable; 2-legged partner OAuth is email-request/partnership only.
- **Workaround that works:** the **TripIt Calendar Feed** (Settings → Calendar Feed → enable → copy the
  **read-only `.ics` URL**) is available to users and refreshes every 15 min–24 h. Consume via the **ICS
  plugin**; tag items `Travel`. This gets flights/hotels/cars/rail as events without API access.
- **Verdict:** ship TripIt as an **ICS-feed integration**, not an API plugin. A true `itinerary.import`
  API plugin only if you obtain partner access.

### Flight pricing — `flight.price` 🟡 (pivot off Amadeus)
- **Blocker:** **Amadeus Self-Service portal is being decommissioned on 2026-07-17.** Do **not** build the
  starter plugin on it (Enterprise APIs remain but are heavyweight/contracted).
- **Recommended:** **Duffel Flights** — modern REST, search **and** book across 300+ airlines (NDC/GDS/
  LCC), official **C#** client library, fast onboarding. **Kiwi (Tequila)** is a strong alternative
  (~750 carriers, flexible/multi-city, startup-friendly). **Skyscanner** = approved commercial partners
  only (no public free tier). **Travelpayouts** = affiliate/commission model, low friction for price
  display + deep-links.
- **Verdict:** lead with **Duffel** (declarative connector or its C# SDK); add Kiwi as a second provider
  behind the fallback aggregator. Booking stays external unless you adopt Duffel ticketing.

### Hotel pricing — `stay.price` 🟡
- **Recommended:** **Duffel Stays** (millions of properties, search/book/manage, profit-share, same SDK)
  pairs cleanly with Duffel Flights. Hotelbeds/Expedia Rapid/Booking Demand are partner-gated alternatives.
- **Blockers:** hotel content/pricing APIs are partner-gated and ToS-restrict caching → per-plugin rate
  budgets + graceful degradation (already in the aggregator design).

---

## Net changes to the plan
- **Travel pricing:** replace Amadeus-as-primary with **Duffel** (flights + stays); keep Kiwi as a
  fallback. Amadeus only if Enterprise access is acquired.
- **TripIt & Proton:** both are **ICS-feed integrations**, not API plugins — the ICS plugin (Phase 1)
  unlocks both, so they need no new auth work.
- **Geocoding:** default to **self-hosted Nominatim** (or a commercial geocoder); never the public
  Nominatim endpoint in a shipped app — see its usage policy.
- **Routing:** **self-hosted OSRM/Valhalla** as the free default; **Google Directions** as an optional
  paid plugin for transit/traffic-aware "leave by" timing.

## Sources
- Proton CalDAV (none, by design): <https://blog.mailfence.com/proton-calendar-support-caldav/>, <https://protonmail.uservoice.com/forums/284483-proton-mail-calendar/suggestions/49566101-caldav>
- Proton share-via-link ICS: <https://proton.me/support/share-calendar-via-link>
- TripIt API (closed to new integrations): <https://help.tripit.com/en/support/solutions/articles/103000391296-tripit-public-api>, <https://tripit.github.io/api/doc/v1/>
- TripIt calendar feed (.ics): <https://help.tripit.com/en/support/solutions/articles/103000063280-calendar-feed-setup-and-sync>
- Amadeus self-service sunset 2026-07-17 & free tier: <https://developers.amadeus.com/self-service>, <https://developers.amadeus.com/pricing>
- Duffel Flights & Stays: <https://duffel.com/>, <https://duffel.com/stays>, <https://duffel.com/docs>
- Flight API alternatives (Kiwi/Skyscanner/Travelpayouts): <https://www.scrapingbee.com/blog/top-flights-apis-for-travel-apps/>, <https://thunderbit.com/blog/best-flight-api-with-free-tiers>
- Nominatim usage policy: <https://operations.osmfoundation.org/policies/nominatim/>
