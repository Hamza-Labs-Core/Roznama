# API contracts (host ⇄ UI)

The Blazor WASM UI talks to the ASP.NET Core host over a small REST surface. The host hides plugins
behind capabilities — the UI never calls a provider directly. This document specifies the endpoints,
shapes, and conventions. (Internal/local API; not a public API. Versioned at `/api/v1`.)

- [Conventions](#conventions)
- [Plugins & capabilities](#plugins--capabilities)
- [Accounts & connect flow](#accounts--connect-flow)
- [Calendars & categories](#calendars--categories)
- [Events (the projected stream)](#events-the-projected-stream)
- [Duplicates](#duplicates)
- [Places, geocoding & routing](#places-geocoding--routing)
- [Trips & fares](#trips--fares)
- [Sharing](#sharing)
- [Sync](#sync)

---

## Conventions

- **Base:** `/api/v1`. JSON only. Times are **ISO-8601 UTC** (`2026-06-03T14:00:00Z`); all-day uses a
  date (`2026-06-03`).
- **Auth:** local session cookie (single-user host) or bearer for the optional cloud node. Secrets never
  cross this boundary — the UI never sees tokens.
- **Errors:** RFC 9457 problem+json — `{ type, title, status, detail, errors? }`.
- **Lists:** cursor pagination `{ items, nextCursor? }`; pass `?cursor=`.
- **Concurrency:** mutable resources return `ETag`; send `If-Match` on writes.
- **IDs:** GUID strings.

## Plugins & capabilities

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/plugins` | Installed plugins: `{ id, name, version, kind, state, capabilities[], authScheme, faultReason? }`. |
| `GET` | `/plugins/{id}` | One plugin incl. JSON Schema for its config (`configSchema`, `configRequired`) + auth scheme. |
| `POST` | `/plugins/install` | Install from `{ downloadUrl, sha256?, signature?, publisherName? }`; integrity + signature + trust gates. |
| `GET` | `/marketplace?url=` | A registry index's entries (or `Marketplace:RegistryUrl` when configured). |
| `DELETE` | `/plugins/{id}` | Uninstall (unloads its ALC); `409` while accounts still use it. |
| `GET` | `/capabilities` | Map of `capability → [pluginId]` (drives "what can the app do right now"). |

The `configSchema` is what the UI renders as the dynamic settings/connect form (see [PLUGINS.md §9](PLUGINS.md#9-schema-driven-configuration-ui)).

## Accounts & connect flow

Adding an account is plugin-driven: read the plugin's auth scheme, run it, store the result.

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/accounts` | All connected accounts `{ id, pluginId, displayName, status, lastSyncAt, priority }`. |
| `POST` | `/accounts` | Begin connect: `{ pluginId, config }`. Returns either a finished account or an `authChallenge`. |
| `GET` | `/accounts/{id}` | One account + its calendars. |
| `PATCH` | `/accounts/{id}` | Rename, set **priority** (drives dedup canonical), enable/disable. |
| `DELETE` | `/accounts/{id}` | Disconnect; purges cached data + vault entry. |
| `POST` | `/accounts/{id}/sync` | Force a sync now. |

**OAuth (PKCE) connect** — host owns the dance:

```
POST /accounts {pluginId:"...google...", config:{}}
  → 200 { id, authChallenge: { kind:"oauth2", redirectUrl, state } }   # UI opens redirectUrl
GET  /accounts/oauth/callback?code=...&state=...            # host exchanges code, stores token
  → 302 back to app; account now status:"connected"
```

**App-password / API-key connect** (CalDAV, some declarative plugins) — single step:

```
POST /accounts {pluginId:"...caldav...", config:{serverUrl, username}, secret:{appPassword}}
  → 201 { id, displayName, calendars:[...] }                # secret goes straight to the vault
```

## Calendars & categories

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/calendars` | All calendars across accounts `{ id, accountId, name, color, isVisible, isReadOnly }`. |
| `PATCH` | `/calendars/{id}` | Toggle `isVisible`, override `color`. |
| `GET` | `/categories` | `{ id, name, isVisible, matchRules }`. |
| `POST`/`PATCH`/`DELETE` | `/categories[/{id}]` | Manage categories + their visibility (e.g. hide Birthdays). |

## Events (the projected stream)

The core read endpoint. Returns the **filtered, deduped, recurrence-expanded** stream for a window — the
single source every view renders from.

```
GET /events?from=2026-06-01&to=2026-09-30
            &calendars=ID,ID            # optional explicit subset (default: all visible)
            &categories=ID,ID           # optional
            &includeDuplicates=false    # default false (canonical only)
            &includeBusy=false          # add free/busy blocks layer
            &expandRecurrence=true
```

```jsonc
{
  "items": [{
    "id": "guid", "calendarId": "guid", "uid": "ical-uid",
    "title": "Team offsite", "startUtc": "2026-07-02T08:00:00Z", "endUtc": "2026-07-02T17:00:00Z",
    "allDay": false, "color": "#3b82f6",
    "categories": ["Work"],
    "place": { "id":"guid", "label":"Berlin HQ", "lat":52.52, "lng":13.40 },
    "duplicateGroupId": null, "duplicateCount": 0,
    "isRecurringInstance": true, "masterId": "guid"
  }],
  "nextCursor": null
}
```

Writes (gated behind `calendar.write`): `POST /events`, `PATCH /events/{id}`, `DELETE /events/{id}` —
the host routes to the owning calendar's plugin; offline edits queue and replay.

## Duplicates

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/events/{id}/duplicates` | The group's members with provenance + the canonical marked. |
| `GET` | `/duplicates/overrides` | Active user overrides `{ id, kind, uids[], reason?, createdAtUtc }`. |
| `POST` | `/duplicates/overrides` | Create one: `{ kind: ForceMerge\|NeverMerge\|SetCanonical, uids[], reason? }`. |
| `DELETE` | `/duplicates/overrides/{id}` | Undo (tombstone) — the automatic grouping returns. |

Overrides bind by **iCal UID** (not row id) so they survive a provider re-sync, and every mutation
regroups immediately (see [ARCHITECTURE §12](ARCHITECTURE.md#12-duplicates)).

## Places, geocoding & routing

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/places/search?q=` | `geo.places` autocomplete → `[{ label, lat, lng, source }]`. |
| `POST` | `/geocode` | `{ query }` → `{ lat, lng, source, cached }` (`geo.geocode`, aggregator). |
| `POST` | `/route` | Travel-time between points (below). |
| `GET` | `/map/events?from=&to=®ion=` | Geocoded events for the **map view** (pins/clusters/routes). |
| `GET` | `/tiles/style` | Active `geo.tiles` style URL/config for MapLibre. |

**Route / travel-time** — powers commute chips, "leave by", conflict detection:

```
POST /route
{ "from": {"lat":52.52,"lng":13.40}, "to": {"lat":52.50,"lng":13.45},
  "mode": "transit", "arriveBy": "2026-07-02T09:00:00Z" }
→ { "durationSec": 1920, "leaveByUtc": "2026-07-02T08:28:00Z",
    "mode": "transit", "source": "google", "geometry": "<encoded polyline>",
    "feasible": true }            # feasible=false ⇒ UI shows "you can't make it"
```

The planner also exposes precomputed legs: `GET /events/{id}/commute` → the `RouteLeg` to the next event.

## Trips & fares

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/trips?from=&to=` | Trips + items (booked + candidate drafts). |
| `POST` | `/trips` / `PATCH` / `DELETE` | Manage trips & scenario drafts. |
| `POST` | `/fares/flights/search` | `{ from, to, dateRange, pax }` → offers (aggregated, deduped, cheapest tagged w/ source + deepLink). |
| `POST` | `/fares/stays/search` | `{ place, checkIn, checkOut, guests }` → stay offers. |
| `GET` | `/fares/overlay?...` | Cheapest-date (flights) / nightly-rate (stays) badges for the multi-month grid. |
| `GET`/`POST`/`DELETE` | `/fares/watches[/{id}]` | Manage `FareWatch`es; `GET /{id}/history` → price series. |

## Sharing

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/shares` | Active shares. |
| `POST` | `/shares` | `{ calendarId|filter, scope:"FullDetails"|"FreeBusy", expiresAt? }` → `{ token, url }`. |
| `DELETE` | `/shares/{id}` | Revoke. |
| `GET` | `/share/{token}.ics` | **Public** live ICS feed for the share (no auth; honors scope). |
| `GET` | `/share/{token}` | **Public** read-only web view. |

## Sync

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/sync/status` | Per-account `{ accountId, displayName, pluginId, accountStatus, state, lastSyncAtUtc, nextRunAtUtc, backoffUntilUtc?, attempts, lastError? }`. |
| `POST` | `/sync/run?force=` | Trigger a sweep (force ignores due/backoff). `POST /accounts/{id}/sync` syncs one account. |
| `GET` | `/sync/stream` (SSE) | Server-sent events: live `eventsChanged` / `syncProgress` / `notificationsChanged`. |
| `GET` | `/cloud/status` | This device's enrollment `{ enabled, relayUrl, spaceId, lastPulledSeq, lastSyncAtUtc }`. |
| `POST` | `/cloud/enable` | Opt in: `{ passphrase, relayUrl, spaceId?, spaceToken? }` — omit space fields to create a new space (the token is returned ONCE); pass them to join from another device. The key derives client-side; the relay stores ciphertext only. |
| `POST` | `/cloud/sync` | Run one push+pull round now. `POST /cloud/disable` forgets the enrollment. |

> The SSE stream is what keeps views live: a background sync delta pushes `eventsChanged{range}` and the
> UI re-projects only affected cells/legs (see [UI.md §9](UI.md#9-performance)).
