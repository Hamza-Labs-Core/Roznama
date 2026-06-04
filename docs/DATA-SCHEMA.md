# Data schema (EF Core + SQLite)

This document is the **authoritative, implementation-ready relational schema** for Unified Calendar's
on-device store. It refines the ERD in [ARCHITECTURE.md §9](ARCHITECTURE.md#9-domain-model) into concrete
tables, columns, types, constraints, and indexes; specifies the EF Core 9 mapping (value converters, JSON
columns, owned vs referenced types, concurrency tokens, SQLCipher); and pins the recurrence model and the
query→index mapping the read paths depend on.

It is a **spec, not code** — the C# sketches in §9 are illustrative anchors, not a project. Names are kept
**exactly** consistent with [ARCHITECTURE.md §9](ARCHITECTURE.md#9-domain-model), the REST shapes in
[API.md](API.md), the plugin/auth model in [PLUGINS.md](PLUGINS.md), and the normalization tables in the
[deep dives](deep-dives/) (Google, ICS, CalDAV, Graph, geo-routing, geo-geocoding/places, travel-fares).

- [1. Conventions](#1-conventions)
- [2. Table catalog](#2-table-catalog)
  - [2.1 Plugins & accounts](#21-plugins--accounts)
  - [2.2 Secrets vault](#22-secrets-vault)
  - [2.3 Calendars, events, categories](#23-calendars-events-categories)
  - [2.4 Duplicates](#24-duplicates)
  - [2.5 Places, geocoding, routing](#25-places-geocoding-routing)
  - [2.6 Trips, fares, drafts](#26-trips-fares-drafts)
  - [2.7 Sharing](#27-sharing)
  - [2.8 Sync, outbox, cloud E2E metadata](#28-sync-outbox-cloud-e2e-metadata)
- [3. Recurrence model](#3-recurrence-model)
- [4. Key query → index mapping](#4-key-query--index-mapping)
- [5. EF Core specifics](#5-ef-core-specifics)
- [6. C# sketches](#6-c-sketches)
- [7. Migration strategy](#7-migration-strategy)
- [8. ER diagram](#8-er-diagram)
- [Sources](#sources)

---

## 1. Conventions

These rules are **global**; per-table sections only call out exceptions.

| Concern | Rule |
| --- | --- |
| **Primary keys** | GUID, stored as **`TEXT`** (lower-case 36-char `D` format, e.g. `8f3a…`). CLR `Guid`. Client-generated (`Guid.CreateVersion7()` / v7 for time-ordered locality where insert order matters; v4 elsewhere). Stored as TEXT — never BLOB — for human-readable diffs, portable share/cloud payloads, and trivial `sqlite3` debugging. |
| **Timestamps** | Every instant is **UTC**, stored as **ISO-8601 text** with offset `+00:00` (`TEXT`, CLR `DateTimeOffset`), via a value converter (§5). Column suffix **`Utc`** (e.g. `StartUtc`, `LastSyncAtUtc`). Never store local time. **All-day** dates are date-only (`TEXT` `yyyy-MM-dd`, CLR `DateOnly`) and carry **no** zone — see §3. |
| **Enums** | Stored **as string** (`TEXT`), not ordinal, so values are stable across reorderings and self-documenting in the DB. Converter `EnumToStringConverter<T>` (§5). E.g. `TripItem.Kind = "Flight"`, `Share.Scope = "FreeBusy"`. |
| **Booleans** | `INTEGER` 0/1 (SQLite has no native bool), CLR `bool`. EF maps this by default. |
| **Money** | `decimal` ⇒ SQLite `TEXT` (EF default for `decimal` on SQLite, to avoid `REAL` precision loss). Currency is a separate `TEXT` ISO-4217 column. **Never** use `REAL` for prices. |
| **Lat/Lng** | `REAL` (CLR `double`) — coordinates tolerate float; precision ~7 dp is sub-cm. |
| **Naming** | Tables **PascalCase singular** (`Event`, `RouteLeg`); columns PascalCase; FKs `<Entity>Id`; join tables `<A><B>` (`EventCategory`). Indexes `IX_<Table>_<Col1>_<Col2>`; unique `UX_<Table>_<Cols>`; FKs `FK_<Table>_<Ref>`. |
| **Soft-delete / tombstones** | **Nothing the user can see is hard-deleted.** Provider-cache rows (`Event`, `Calendar`) are removed when the provider reports a deletion (provider owns truth; re-syncs are idempotent). **User-metadata** rows that participate in cloud sync (categories, dedup overrides, scenario drafts, trips, fare watches, shares, calendar/place overrides) carry an **`IsDeleted`** tombstone + `UpdatedAtUtc` and are **never physically removed** while sync is enabled, so a delete propagates as a last-writer-wins tombstone rather than a silent disappearance (ARCHITECTURE §8/§10). A background compactor may purge tombstones older than the sync horizon. Suppression in dedup/visibility is a **view-layer** decision (`IsCanonical`/`IsVisible`), never a delete. |
| **Sync-metadata columns** | Every **user-metadata** table (the ones cloud sync replicates) carries the four columns in [§2.8](#28-sync-outbox-cloud-e2e-metadata): `UpdatedAtUtc`, `DeviceId`, `Lamport`, `IsDeleted`. Provider-cache tables (`Event`, `Calendar`, `GeocodeCache`, `RouteLeg`, `FareSample`) do **not** — they refresh from the provider and are not E2E-replicated as authoritative state. |
| **Concurrency** | Local optimistic concurrency uses a **`RowVersion`** token (`TEXT`, a GUID re-stamped on every write — SQLite has no native `rowversion`; see §5). Provider-cache rows additionally store the provider's **`ETag`**/token for upstream concurrency (Google/CalDAV/Graph `If-Match`/`412`, Google `etag`). The REST `ETag`/`If-Match` in [API.md](API.md#conventions) projects `RowVersion`. |
| **Strings** | `TEXT`, UTF-8 (SQLite default). No length caps at the DB layer (SQLite ignores `VARCHAR(n)`); validation lives in the domain layer. |
| **JSON bags** | Flexible/open-ended structures (plugin manifest, config, category `matchRules`, recurrence overrides, version vectors) are **`TEXT` JSON columns** mapped via EF Core 9 `ToJson()` owned types or a JSON value converter (§5). |

> **GUID-as-TEXT trade-off.** TEXT GUIDs cost ~36 bytes vs 16 for BLOB and index slightly larger. We accept
> that for debuggability and because the E2E cloud payload and `.ics` share feeds are text anyway; the hot
> read path is bounded by the **composite covering indexes** in §4, not by PK width.

---

## 2. Table catalog

Legend for the **Null?** column: `N` = NOT NULL, `Y` = nullable. "SQLite + CLR" gives the storage affinity
and the mapped .NET type. Every user-metadata table's four sync columns are defined once in
[§2.8](#28-sync-outbox-cloud-e2e-metadata) and only referenced (not repeated) per table to keep the catalog
readable; assume them present where the table notes "**+ sync-metadata**".

### 2.1 Plugins & accounts

#### `Plugin` — installed plugins (manifest registry; ARCHITECTURE §4, PLUGINS §3)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `string` | N | **PK.** Reverse-DNS id from `plugin.yaml` (e.g. `org.unifiedcalendar.google`). Natural key, not a GUID — matches PLUGINS §3 / manifest `id`. |
| `Name` | TEXT / `string` | N | Display name. |
| `Version` | TEXT / `string` | N | SemVer plugin version. |
| `SdkVersion` | TEXT / `string` | N | Compatible SDK range (`"1.x"`). |
| `Kind` | TEXT / `PluginKind` enum | N | `Declarative` \| `Assembly`. |
| `Status` | TEXT / `PluginStatus` enum | N | `Installed` \| `Disabled` \| `Error`. |
| `Capabilities` | TEXT(JSON) / `string[]` | N | JSON array of capability ids (`["calendar.read"]`) — owned JSON (§5). |
| `Manifest` | TEXT(JSON) / `PluginManifest` | N | Full parsed manifest (auth scheme, `network.allow`, `config` schema, `operations`, `presets`) as a JSON column — flexible bag, never queried relationally. |
| `TrustTier` | TEXT / `TrustTier` enum | N | `InBox` \| `Signed` \| `Community` \| `LocalDev` (PLUGINS §8). |
| `InstalledAtUtc` | TEXT / `DateTimeOffset` | N | |

- **PK** `Id`. **Indexes:** none beyond PK (small table, scanned in full).
- Not E2E-synced as authoritative user state (plugin install is device-local); no sync-metadata columns.

#### `PluginConfig` — per-plugin non-secret settings (rendered from the manifest `config` JSON Schema)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `PluginId` | TEXT / `string` | N | **FK → `Plugin.Id`** (cascade delete). |
| `Values` | TEXT(JSON) / `JsonDocument` | N | Config object validated against the manifest schema (e.g. `{ syncWindowMonthsPast: 12 }`). **Secrets never go here** — they go to `SecretRef` (§2.2). |
| *+ sync-metadata* | | | User-set preferences replicate. |

- **PK** `Id`. **FK** `FK_PluginConfig_Plugin` (`PluginId`). **Unique** `UX_PluginConfig_PluginId` (one config row per plugin).

#### `Account` — a connected provider account (ARCHITECTURE §9, API §accounts)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `PluginId` | TEXT / `string` | N | **FK → `Plugin.Id`** (restrict — can't drop a plugin with live accounts). |
| `DisplayName` | TEXT / `string` | N | User-visible label. |
| `AuthRef` | TEXT / `Guid` | Y | **FK → `SecretRef.Id`** — the vault handle for this account's token/credential. Null for `auth.scheme=none` (e.g. some ICS feeds, though the feed URL itself is still a secret stored via `SecretRef`). |
| `Priority` | INTEGER / `int` | N | User-orderable; **drives dedup canonical** (ARCHITECTURE §12, API `PATCH /accounts`). Lower = higher priority. |
| `Status` | TEXT / `AccountStatus` enum | N | `Connected` \| `NeedsAuth` \| `Disabled` \| `Error`. |
| `LastSyncAtUtc` | TEXT / `DateTimeOffset` | Y | Last successful sync. Mirrors `SyncState` for the quick account list. |
| *+ sync-metadata* | | | Account identity/prefs replicate (token does **not** — see §2.2). |

- **PK** `Id`. **FKs** `FK_Account_Plugin` (`PluginId`, restrict), `FK_Account_SecretRef` (`AuthRef`, set-null).
- **Index** `IX_Account_PluginId`. **Index** `IX_Account_Priority` (dedup canonical ordering).
- Per the ERD, `Account` historically carried `SyncToken`/`LastSyncAt`; those move to the dedicated
  **`SyncState`** table (§2.8) which is **per account *and* per calendar** (Google/CalDAV hold a token per
  calendar, not per account). `Account.LastSyncAtUtc` is a denormalized convenience mirror.

### 2.2 Secrets vault

#### `SecretRef` — encrypted credential vault entry (PLUGINS §6, ARCHITECTURE §17)

The **only** place tokens/credentials/feed-URL secrets live. Stores **ciphertext + key id**, never
plaintext. Tokens **stay on the authorizing device** and are **never E2E-synced** (ARCHITECTURE §8).

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** Referenced by `Account.AuthRef`. |
| `Kind` | TEXT / `SecretKind` enum | N | `OAuthRefreshToken` \| `OAuthAccessToken` \| `ApiKey` \| `BasicPassword` \| `AppPassword` \| `FeedUrl` (ICS capability URL — treated as a bearer secret, ICS deep-dive §3). |
| `KeyId` | TEXT / `string` | N | Identifier of the **wrapping key** (DPAPI/Keychain/KMS/master-key id) used to encrypt `Ciphertext`. Lets keys rotate without re-deriving — decrypt selects the key by id. **No key material is ever stored here.** |
| `Algorithm` | TEXT / `string` | N | AEAD scheme tag (e.g. `AES-256-GCM`). |
| `Nonce` | BLOB / `byte[]` | N | Per-secret random IV/nonce. |
| `Ciphertext` | BLOB / `byte[]` | N | AEAD ciphertext of the secret value. |
| `AuthTag` | BLOB / `byte[]` | Y | AEAD tag (if not appended to `Ciphertext`). |
| `Aad` | TEXT / `string` | Y | Associated data bound into the AEAD (e.g. `accountId` + `kind`) to prevent ciphertext swapping. |
| `ExpiresAtUtc` | TEXT / `DateTimeOffset` | Y | For short-lived access tokens; broker refreshes before expiry. |
| `RotatedAtUtc` | TEXT / `DateTimeOffset` | N | Last rotation. |
| `RowVersion` | TEXT / `string` | N | Concurrency token. |

- **PK** `Id`. **Index** `IX_SecretRef_Kind`. No sync-metadata columns — **device-local by policy**.
- Hard invariant (CI-asserted): no column ever holds a plaintext secret; the only secret bytes are inside
  `Ciphertext`. Audit log redacts `Ciphertext`/`Nonce`. See [§5](#5-ef-core-specifics) for SQLCipher (the
  whole DB is also encrypted at rest, so this is **defence in depth**: app-layer AEAD inside a SQLCipher file).

### 2.3 Calendars, events, categories

#### `Calendar` — a calendar within an account (provider cache + user overrides)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `AccountId` | TEXT / `Guid` | N | **FK → `Account.Id`** (cascade delete — disconnecting purges its calendars). |
| `RemoteId` | TEXT / `string` | N | Provider calendar id (`primary`, `…@group.calendar.google.com`, CalDAV collection href, ICS feed URL). Stable target for `Events.list`/`sync-collection`. |
| `Name` | TEXT / `string` | N | From `summary` / `displayname` / `X-WR-CALNAME`. |
| `Color` | TEXT / `string` | Y | Provider color; user override allowed (API `PATCH /calendars`). |
| `IsVisible` | INTEGER / `bool` | N | **User override** — replicates. Default 1. |
| `IsReadOnly` | INTEGER / `bool` | N | From `accessRole`/ACL/ICS (ICS always read-only). Gates write affordances. |
| `Kind` | TEXT / `CalendarKind` enum | Y | `Primary` \| `Holidays` \| `Birthdays` \| `Subscribed` \| `Generic` — hints dedup/category rules (Google deep-dive §4); plugin must **not** drop Holidays/Birthdays. |
| *+ sync-metadata* | | | Only the **user-override** fields (`Color`, `IsVisible`) are LWW-merged; provider fields refresh on sync. |

- **PK** `Id`. **FK** `FK_Calendar_Account` (`AccountId`, cascade).
- **Unique** `UX_Calendar_Account_RemoteId` (`AccountId`, `RemoteId`) — idempotent upsert key.
- **Index** `IX_Calendar_AccountId`.

#### `Event` — the central entity (provider cache; master + recurrence overrides; ARCHITECTURE §9, §3 here)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** Local surrogate. |
| `CalendarId` | TEXT / `Guid` | N | **FK → `Calendar.Id`** (cascade). |
| `Uid` | TEXT / `string` | N | iCalendar `UID` / Google `iCalUID`. **Stable cross-system id — feeds dedup** (holidays/birthdays collapse). |
| `RemoteId` | TEXT / `string` | N | Provider resource id: Google `id`, CalDAV `href`, ICS `(UID, RECURRENCE-ID?)`. Differs per occurrence; unique within calendar. |
| `Title` | TEXT / `string` | Y | `SUMMARY` / `summary`. Null for limited-view "busy" blocks (ICS deep-dive §11). |
| `StartUtc` | TEXT / `DateTimeOffset` | N | Resolved to UTC. For all-day, the date at 00:00 (see `AllDay`). **Hot-path column** (§4). |
| `EndUtc` | TEXT / `DateTimeOffset` | N | UTC. All-day end is **inclusive** in domain semantics (subtract the RFC-5545 exclusive `DTEND`/Google exclusive `end.date` at ingest). **Hot-path column** (§4). |
| `AllDay` | INTEGER / `bool` | N | True ⇒ `Start`/`End` are floating dates, no zone (`DTSTART;VALUE=DATE` / Google `start.date`). |
| `StartDate` | TEXT / `DateOnly` | Y | Set only when `AllDay` — the floating date, to avoid UTC-shift off-by-one bugs. `StartUtc` still populated (00:00Z) for range queries. |
| `Rrule` | TEXT / `string` | Y | The master's `RRULE` (+ `RDATE`/`EXDATE` serialized). **Null for non-recurring and for override rows.** Expanded on demand with Ical.Net (§3). |
| `MasterId` | TEXT / `Guid` | Y | **Self-FK → `Event.Id`.** Set on **override** rows (a moved/edited occurrence); null on masters and singletons (§3). |
| `RecurrenceId` | TEXT / `DateTimeOffset` | Y | The `RECURRENCE-ID` / Google `originalStartTime` — which occurrence this override replaces (§3). Null unless an override. |
| `PlaceId` | TEXT / `Guid` | Y | **FK → `Place.Id`** (set-null). Resolved from `Location`/`GEO` via `geo.geocode`. |
| `Location` | TEXT / `string` | Y | Raw free-form location text (kept even when geocoding fails, for retry). |
| `DedupSignature` | TEXT / `string` | Y | `hash(normalizedTitle \| startDate \| allDay \| endDate?)` (ARCHITECTURE §12). Null until computed. **Grouping key** (§4). |
| `DuplicateGroupId` | TEXT / `Guid` | Y | **FK → `DuplicateGroup.Id`** (set-null). |
| `Status` | TEXT / `EventStatus` enum | N | `Confirmed` \| `Tentative` \| `Cancelled`. `Cancelled` ⇒ delete/EXDATE semantics from the provider. |
| `ETag` | TEXT / `string` | Y | Provider concurrency token (Google `etag`, CalDAV ETag) for `If-Match`/`412` write-back. |
| `LastModifiedUtc` | TEXT / `DateTimeOffset` | Y | Provider `updated` / `LAST-MODIFIED` — secondary change signal for diffing (ICS deep-dive §7). |
| `RowVersion` | TEXT / `string` | N | Local concurrency token. |

- **PK** `Id`. **FKs:** `FK_Event_Calendar` (cascade), `FK_Event_Place` (set-null), `FK_Event_DuplicateGroup`
  (set-null), `FK_Event_Master` (`MasterId` → `Event.Id`, restrict/no-cascade to avoid recursive cascade
  surprises — overrides are removed explicitly with their master).
- **Unique** `UX_Event_Calendar_RemoteId` (`CalendarId`, `RemoteId`) — **idempotent upsert key** for sync
  (Google `id`, CalDAV href, ICS `(UID,RECURRENCE-ID)`). Override rows have distinct `RemoteId`.
- **Indexes:** see [§4](#4-key-query--index-mapping) for the hot-path composites
  (`IX_Event_Calendar_Time`, `IX_Event_DedupSignature`, `IX_Event_Master`, `IX_Event_Uid`,
  `IX_Event_PlaceId`).
- **Not** E2E-synced as authoritative state (provider owns it); optionally cached to other devices for
  instant view (ARCHITECTURE §8), but the source of truth is the provider, so no sync-metadata columns.

#### `Category` — a category/tag with visibility (ARCHITECTURE §13)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Name` | TEXT / `string` | N | `Work`, `Birthday`, `Holiday`, `Travel`, … |
| `IsVisible` | INTEGER / `bool` | N | One toggle hides all matching events (ARCHITECTURE §13). |
| `MatchRules` | TEXT(JSON) / `MatchRuleSet` | Y | Source-derived + user rules (keywords, calendar-of-origin, provider category names) as a JSON bag — owned JSON (§5). |
| `IsBuiltIn` | INTEGER / `bool` | N | Seeded categories (§7) can't be deleted, only hidden. |
| *+ sync-metadata* | | | User categories + visibility replicate. |

- **PK** `Id`. **Unique** `UX_Category_Name`. **Index** `IX_Category_IsVisible` (visibility pipeline §4).

#### `EventCategory` — join (Event ↔ Category, many-to-many)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `EventId` | TEXT / `Guid` | N | **FK → `Event.Id`** (cascade). |
| `CategoryId` | TEXT / `Guid` | N | **FK → `Category.Id`** (cascade). |
| `AssignedBy` | TEXT / `AssignmentSource` enum | N | `Rule` \| `User` — so a user assignment survives re-evaluation of source rules. |

- **PK** composite (`EventId`, `CategoryId`). **Indexes:** PK covers `EventId` lookups; add
  `IX_EventCategory_CategoryId` for "all events in category" (visibility/hide).
- Rule-derived rows are re-derivable, so this table is **not** sync-replicated; `AssignedBy=User`
  overrides ride `Event` cache rules or are reconstructed from synced `Category.MatchRules`.

### 2.4 Duplicates

#### `DuplicateGroup` — a set of events sharing a signature (ARCHITECTURE §12)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Signature` | TEXT / `string` | N | The shared dedup signature. |
| `CanonicalEventId` | TEXT / `Guid` | Y | **FK → `Event.Id`** (set-null) — the event that shows; others suppressed as "+N" (API `POST /duplicates/{groupId}/canonical`). |
| *+ sync-metadata* | | | The chosen canonical is user state — replicates. |

- **PK** `Id`. **Unique** `UX_DuplicateGroup_Signature`. **FK** `FK_DuplicateGroup_Canonical` (set-null).
- The group is **derived** from event signatures but the **canonical choice** is user metadata, so the row
  is sync-replicated even though membership recomputes from `Event.DuplicateGroupId`.

#### `DuplicateOverride` — user merge/split/never-merge metadata (reversible; ARCHITECTURE §12, API §duplicates)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Kind` | TEXT / `OverrideKind` enum | N | `ForceMerge` \| `NeverMerge` \| `SetCanonical` (API `POST /duplicates/merge`, `/split`, `/canonical`). |
| `EventIds` | TEXT(JSON) / `Guid[]` | N | The events the override binds (an unordered set, by `Uid` where possible so it survives re-sync) — owned JSON. |
| `Reason` | TEXT / `string` | Y | Optional user note / explainability. |
| `CreatedAtUtc` | TEXT / `DateTimeOffset` | N | |
| *+ sync-metadata* | | | **Core synced user metadata** (ARCHITECTURE §8 "dedup overrides"). |

- **PK** `Id`. **Index** `IX_DuplicateOverride_Kind`.
- Stored by **`Uid`** inside `EventIds` (not local `Event.Id`) so a never-merge survives provider re-sync
  on another device where local surrogate ids differ.

### 2.5 Places, geocoding, routing

#### `Place` — a resolved location pin (ARCHITECTURE §9; geo-geocoding deep-dive §3)

**Recommendation: `Place` is a SHARED (referenced) entity, not an owned type.** Justification:

- It is referenced by **multiple** owners (`Event.PlaceId`, `TripItem.PlaceId`, `FareWatch.Origin/DestPlaceId`)
  and **deduped by proximity + label** so two events at the same address share **one pin and one
  `geo.route` leg** (geo-geocoding deep-dive §3). An owned type is private to a single owner and cannot be
  shared or deduped — it would duplicate the pin per event and break leg caching and map clustering.
- It has its own identity and lifecycle (re-geocode, manual edit) independent of any one event.

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Label` | TEXT / `string` | N | Display label (`Berlin HQ`). |
| `Lat` | REAL / `double` | N | |
| `Lng` | REAL / `double` | N | |
| `Address` | TEXT / `string` | Y | Parsed/formatted address (`display_name`). |
| `Source` | TEXT / `string` | Y | Winning geocoder id (`nominatim`/`photon`/`google`) for attribution provenance. |
| `NormalizedKey` | TEXT / `string` | Y | Proximity+label dedupe key (rounded `lat,lng` + lower-cased label) so equal places collapse to one row. |
| *+ sync-metadata* | | | A user-pinned/edited place replicates; pure geocode-derived places refresh from cache. |

- **PK** `Id`. **Unique** `UX_Place_NormalizedKey` (proximity dedupe). **Index** `IX_Place_LatLng`
  (`Lat`,`Lng`) for the map view's bounding-box queries (`GET /map/events?region=`).

#### `GeocodeCache` — query→coordinate cache (geo-geocoding deep-dive §3; cache-forever)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** (Surrogate; the hot key is `QueryHash`.) |
| `Query` | TEXT / `string` | N | The **normalized** query (lower-cased, whitespace-collapsed, punctuation-stripped). |
| `QueryHash` | TEXT / `string` | N | SHA-256 of the normalized query (forward) **or** rounded `lat,lng` key (reverse). The **lookup key** (§4). |
| `Lat` | REAL / `double` | N | |
| `Lng` | REAL / `double` | N | |
| `Source` | TEXT / `string` | N | Geocoder id (attribution + ToS-purge provenance). |
| `IsReverse` | INTEGER / `bool` | N | Distinguishes forward vs reverse (click-to-place) entries. |
| `ResolvedAtUtc` | TEXT / `DateTimeOffset` | N | Enables long-TTL refresh and manual re-geocode (geo-geocoding §3). |

- **PK** `Id`. **Unique** `UX_GeocodeCache_QueryHash` (`QueryHash`) — **the cache lookup** (§4): one row per
  normalized query, zero outbound calls on hit. Provider-cache table — **no sync-metadata**.

#### `RouteLeg` — commute between two consecutive events (ARCHITECTURE §9; geo-routing deep-dive §4)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `FromEventId` | TEXT / `Guid` | N | **FK → `Event.Id`** (cascade). |
| `ToEventId` | TEXT / `Guid` | N | **FK → `Event.Id`** (cascade). |
| `Mode` | TEXT / `TravelMode` enum | N | `Drive` \| `Transit` \| `Walk` \| `Bike`. |
| `DurationSec` | INTEGER / `int` | N | Provider's predicted travel time (traffic-aware where supported). |
| `LeaveByUtc` | TEXT / `DateTimeOffset` | Y | **Host-computed:** `arriveBy − DurationSec − buffer` (geo-routing §4). |
| `Feasible` | INTEGER / `bool` | N | **Host-computed:** `gap ≥ DurationSec`. False ⇒ "you can't make it". |
| `Geometry` | TEXT / `string` | Y | Encoded polyline (precision 5; Valhalla precision-6 normalized at ingest) for the map leg line. |
| `Source` | TEXT / `string` | N | Winning routing provider id (`osrm`/`google`/…) for attribution + cache provenance. |
| `ComputedAtUtc` | TEXT / `DateTimeOffset` | N | For staleness / `stale` degradation flag and invalidation. |
| `IsStale` | INTEGER / `bool` | N | True when served as last-known after all providers failed (geo-routing §6). |

- **PK** `Id`. **FKs** `FK_RouteLeg_FromEvent`, `FK_RouteLeg_ToEvent` (both cascade).
- **Unique** `UX_RouteLeg_From_To_Mode` (`FromEventId`, `ToEventId`, `Mode`) — **the cache key** (§4): one
  leg per consecutive pair per mode; `GET /events/{id}/commute` reads it without a provider call.
  Recomputed only when an endpoint `Place` or time moves (geo-routing §5). Provider-cache — no sync-metadata.

### 2.6 Trips, fares, drafts

#### `Trip` — a trip grouping (ARCHITECTURE §9, §14)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Name` | TEXT / `string` | N | |
| `StartUtc` | TEXT / `DateTimeOffset` | N | |
| `EndUtc` | TEXT / `DateTimeOffset` | N | |
| *+ sync-metadata* | | | Trips are user planning state — replicate. |

- **PK** `Id`. **Index** `IX_Trip_Time` (`StartUtc`,`EndUtc`) for `GET /trips?from=&to=`.

#### `TripItem` — a flight/stay/car/rail/activity within a trip (ARCHITECTURE §9, §14; travel-fares §8)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `TripId` | TEXT / `Guid` | N | **FK → `Trip.Id`** (cascade). |
| `Kind` | TEXT / `TripItemKind` enum | N | `Flight` \| `Stay` \| `Car` \| `Rail` \| `Activity`. |
| `Status` | TEXT / `TripItemStatus` enum | N | `Booked` \| `Candidate` (candidate = scenario/draft; booked projects into an `Event` under Travel). |
| `PlaceId` | TEXT / `Guid` | Y | **FK → `Place.Id`** (set-null). |
| `Confirmation` | TEXT / `string` | Y | Booking confirmation code. |
| `StartUtc` | TEXT / `DateTimeOffset` | Y | Item time (flight depart, check-in). |
| `EndUtc` | TEXT / `DateTimeOffset` | Y | |
| `ProjectedEventId` | TEXT / `Guid` | Y | **FK → `Event.Id`** (set-null) — the projected Travel event for a booked item. |
| `Details` | TEXT(JSON) / `JsonDocument` | Y | Provider-specific bag (carrier/flightNo, nightly rate, deep-link, source) — owned JSON. |
| *+ sync-metadata* | | | Trip items (incl. candidate drafts) replicate. |

- **PK** `Id`. **FK** `FK_TripItem_Trip` (cascade), `FK_TripItem_Place` (set-null),
  `FK_TripItem_ProjectedEvent` (set-null). **Index** `IX_TripItem_TripId`. **Index** `IX_TripItem_PlaceId`.

#### `FareWatch` — a tracked route/stay + date range + pax (ARCHITECTURE §9, §14; travel-fares §10)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `Kind` | TEXT / `FareKind` enum | N | `Flight` \| `Stay`. |
| `OriginPlaceId` | TEXT / `Guid` | Y | **FK → `Place.Id`** (set-null). For stays, the stay place. |
| `DestPlaceId` | TEXT / `Guid` | Y | **FK → `Place.Id`** (set-null). |
| `RangeStart` | TEXT / `DateOnly` | N | Date-only (no time/zone). |
| `RangeEnd` | TEXT / `DateOnly` | N | |
| `Pax` | INTEGER / `int` | N | Passengers/guests. |
| `Currency` | TEXT / `string` | N | Display currency for the series. |
| `TargetPrice` | TEXT / `decimal` | Y | Optional user threshold for `notify`. |
| `LastLowPrice` | TEXT / `decimal` | Y | Last detected low (for drop-threshold firing). |
| `IsActive` | INTEGER / `bool` | N | Pauses polling without deleting. |
| *+ sync-metadata* | | | Watches are user state — replicate. |

- **PK** `Id`. **FKs** `FK_FareWatch_Origin`, `FK_FareWatch_Dest` (set-null). **Index**
  `IX_FareWatch_IsActive` (scheduler picks due active watches).

#### `FareSample` — price-history series point (travel-fares §10; API `GET /fares/watches/{id}/history`)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** (v7 for time-ordered insert locality.) |
| `FareWatchId` | TEXT / `Guid` | N | **FK → `FareWatch.Id`** (cascade). |
| `SampledAtUtc` | TEXT / `DateTimeOffset` | N | Poll timestamp. **Hot-path** (§4). |
| `Price` | TEXT / `decimal` | N | Cheapest at this poll. |
| `Currency` | TEXT / `string` | N | |
| `Source` | TEXT / `string` | N | Winning provider (`duffel`/`kiwi`). |
| `IsStale` | INTEGER / `bool` | N | True when the point is a degraded last-known (travel-fares §9). |
| `Detail` | TEXT(JSON) / `JsonDocument` | Y | Carrier/flightNo/deepLink snapshot for the overlay/chart — owned JSON. |

- **PK** `Id`. **FK** `FK_FareSample_Watch` (cascade).
- **Index** `IX_FareSample_Watch_Time` (`FareWatchId`, `SampledAtUtc` DESC) — **the series query** (§4):
  chart + overlay read newest-first per watch. Provider-derived history — **no sync-metadata** (it is an
  indicative timestamped series, not E2E-replicated authoritative state; travel-fares §11).

#### `ScenarioDraft` — tentative planning items (ARCHITECTURE §6 scenario/draft layer; API `POST /trips`)

The draft layer for tentative trip plans painted over the planner before they become `TripItem`s/`Event`s.

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `TripId` | TEXT / `Guid` | Y | **FK → `Trip.Id`** (set-null) — optional grouping. |
| `Title` | TEXT / `string` | N | |
| `StartUtc` | TEXT / `DateTimeOffset` | N | Candidate slot. |
| `EndUtc` | TEXT / `DateTimeOffset` | N | |
| `PlaceId` | TEXT / `Guid` | Y | **FK → `Place.Id`** (set-null). |
| `Kind` | TEXT / `DraftKind` enum | Y | `Event` \| `Flight` \| `Stay` \| `Activity` — what it becomes when promoted. |
| `Notes` | TEXT(JSON) / `JsonDocument` | Y | Free planning bag (linked fare overlay, alternatives) — owned JSON. |
| *+ sync-metadata* | | | Scenario plans are core synced user metadata (ARCHITECTURE §8). |

- **PK** `Id`. **FK** `FK_ScenarioDraft_Trip` (set-null), `FK_ScenarioDraft_Place` (set-null).
- **Index** `IX_ScenarioDraft_Time` (`StartUtc`,`EndUtc`) so drafts paint into the visible window.

### 2.7 Sharing

#### `Share` — a published read-only calendar/filter (ARCHITECTURE §16, API §sharing)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `CalendarId` | TEXT / `Guid` | Y | **FK → `Calendar.Id`** (cascade). Null when the share is a **filtered union** (then `Filter` defines it). |
| `Filter` | TEXT(JSON) / `ShareFilter` | Y | Filter spec for a union share (`calendars[]`/`categories[]`) — owned JSON. |
| `Token` | TEXT / `string` | N | Unguessable capability token; the public feed/web URL path (`/share/{token}.ics`). **Treated as a secret** (high-entropy, redacted in logs). |
| `Scope` | TEXT / `ShareScope` enum | N | `FullDetails` \| `FreeBusy`. |
| `ExpiresAtUtc` | TEXT / `DateTimeOffset` | Y | Null = no expiry; revocation = `IsDeleted` tombstone. |
| *+ sync-metadata* | | | Shares are user state — replicate (so revocation propagates). |

- **PK** `Id`. **Unique** `UX_Share_Token` (`Token`) — the public feed lookup. **FK**
  `FK_Share_Calendar` (cascade). **Index** `IX_Share_CalendarId`.

### 2.8 Sync, outbox, cloud E2E metadata

#### Shared sync-metadata columns (on every **user-metadata** table)

Defined once; every table marked "**+ sync-metadata**" above carries exactly these four columns. They power
the zero-knowledge, last-writer-wins-per-field cloud sync (ARCHITECTURE §8/§10).

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `UpdatedAtUtc` | TEXT / `DateTimeOffset` | N | Wall-clock of last local mutation; LWW tiebreak after `Lamport`. |
| `DeviceId` | TEXT / `Guid` | N | The device that authored the last write (origin id; part of the version vector). |
| `Lamport` | INTEGER / `long` | N | **Lamport clock** — monotone per-device counter; the primary causal-ordering / conflict key (ARCHITECTURE §8 "Lamport clocks"). |
| `IsDeleted` | INTEGER / `bool` | N | **Tombstone.** Soft-delete; replicated so deletions propagate. Default 0. |

> Conflict resolution is **last-writer-wins per user-metadata field** (ARCHITECTURE §8). `Lamport` orders
> writes; `(Lamport, DeviceId, UpdatedAtUtc)` is the deterministic tiebreak. The relay stores only
> **ciphertext + the version vector** and never the cleartext of any of these rows.

#### `SyncState` — per account *and* per calendar sync cursor (ARCHITECTURE §10, API `GET /sync/status`)

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** |
| `AccountId` | TEXT / `Guid` | N | **FK → `Account.Id`** (cascade). |
| `CalendarId` | TEXT / `Guid` | Y | **FK → `Calendar.Id`** (cascade). Null = account-level state; set = per-calendar token (Google/CalDAV hold a token per calendar). |
| `SyncToken` | TEXT / `string` | Y | Provider delta cursor: Google `nextSyncToken`, CalDAV `sync-token`, ICS `etag\|lastmod\|bodyhash` (minted), Graph delta link. **Opaque** to the host. |
| `Ctag` | TEXT / `string` | Y | CalDAV CTag fallback (when `sync-collection` unsupported). |
| `LastSyncAtUtc` | TEXT / `DateTimeOffset` | Y | Last successful run. |
| `NextRunAtUtc` | TEXT / `DateTimeOffset` | Y | Scheduler next due (API `GET /sync/status.nextRunAt`). |
| `State` | TEXT / `SyncRunState` enum | N | `Idle` \| `Running` \| `Backoff` \| `Error`. |
| `LastError` | TEXT / `string` | Y | Last error detail (problem+json `detail`); cleared on success. |
| `BackoffUntilUtc` | TEXT / `DateTimeOffset` | Y | Per-plugin backoff on 429/5xx (ARCHITECTURE §10). |
| `RowVersion` | TEXT / `string` | N | Concurrency token. |

- **PK** `Id`. **FKs** `FK_SyncState_Account` (cascade), `FK_SyncState_Calendar` (cascade).
- **Unique** `UX_SyncState_Account_Calendar` (`AccountId`, `CalendarId`) — one cursor per (account, calendar);
  `CalendarId NULL` collates as a distinct account-level row (SQLite treats NULLs as distinct in unique
  indexes — acceptable since at most one account-level row is ever written).
- Device-local cursor state — **no sync-metadata** (each device tracks its own provider cursors).

#### `WriteOutbox` — offline write-back queue (ARCHITECTURE §8, API "offline edits queue and replay")

Queues `calendar.write` mutations made offline (and route legs / preference writes) for replay when the
owning plugin is reachable. Idempotent, ordered, retried.

| Name | Type (SQLite + CLR) | Null? | Notes |
| --- | --- | --- | --- |
| `Id` | TEXT / `Guid` | N | **PK.** v7 for FIFO ordering. |
| `EntityType` | TEXT / `string` | N | Target table (`Event`, `Calendar`, …). |
| `EntityId` | TEXT / `Guid` | N | Target row. |
| `Operation` | TEXT / `OutboxOp` enum | N | `Create` \| `Update` \| `Delete`. |
| `Payload` | TEXT(JSON) / `JsonDocument` | N | The mutation (partial event, patch) to replay through the plugin — owned JSON. |
| `BaseETag` | TEXT / `string` | Y | The `If-Match`/ETag captured at queue time for optimistic write-back (CalDAV/Google `412` path). |
| `Status` | TEXT / `OutboxStatus` enum | N | `Pending` \| `InFlight` \| `Failed` \| `Done` \| `Conflict`. |
| `Attempts` | INTEGER / `int` | N | Retry count (backoff). |
| `LastError` | TEXT / `string` | Y | |
| `EnqueuedAtUtc` | TEXT / `DateTimeOffset` | N | |
| `RowVersion` | TEXT / `string` | N | |

- **PK** `Id`. **Index** `IX_WriteOutbox_Status_Enqueued` (`Status`, `EnqueuedAtUtc`) — the replay worker
  pulls `Pending` in FIFO order. Device-local queue — no sync-metadata.

---

## 3. Recurrence model

**Occurrences are NOT materialized.** This is consistent across the Google (§7), CalDAV (§8), and ICS (§8)
plugins, which all hand the host masters + RRULE and rely on Ical.Net for on-demand expansion.

**Storage shape:**

1. **Master event** — one `Event` row with `Rrule` set (the `RRULE` + serialized `RDATE`/`EXDATE`),
   `MasterId = NULL`, `RecurrenceId = NULL`. A 10-year weekly event is **one row**, not 520.
2. **Override events** — a moved/edited single occurrence is its **own** `Event` row with:
   - `MasterId` → the master `Event.Id` (self-FK),
   - `RecurrenceId` = the original occurrence start (`RECURRENCE-ID` / Google `originalStartTime`),
   - `Rrule = NULL` (an override is a concrete instance, not a rule),
   - its own provider `RemoteId` (so the `UX_Event_Calendar_RemoteId` upsert key stays unique — ICS keys
     overrides by `(UID, RECURRENCE-ID)`).
3. **Cancellations** — `EXDATE` (whole-series occurrence removal) is folded into the master's `Rrule`
   serialization; a cancelled single instance arrives as a `Status=Cancelled` override or an `EXDATE`
   (Google "cancelled instance" → occurrence removal, **not** a series delete — Google deep-dive §8).

**Expansion (read path):** the projection service expands `[from, to)` per visible range with
`CalendarEvent.GetOccurrences(from, to)` (Ical.Net), which applies `EXDATE`/`RDATE` and **splices in**
`RecurrenceId` overrides automatically. The REST `GET /events?...&expandRecurrence=true` projection
([API.md](API.md#events-the-projected-stream)) returns expanded instances tagged
`isRecurringInstance: true` + `masterId`, exactly the shape the UI consumes. Switching views never
re-expands the provider — it re-projects from the cached masters/overrides.

**Columns that enable this:** `Event.Rrule`, `Event.MasterId` (self-FK), `Event.RecurrenceId`,
`Event.Status`, plus the time columns `Event.StartUtc`/`Event.EndUtc`.

**Time-range query index.** The expansion needs every master/singleton/override whose **rule or instance
could touch the window**. The driving index is the composite in §4:

```
CREATE INDEX IX_Event_Calendar_Time ON Event (CalendarId, StartUtc, EndUtc);
```

- **Non-recurring & overrides** are pruned directly by `StartUtc < to AND EndUtc > from`.
- **Masters** (`Rrule IS NOT NULL`) can recur into the window from a `StartUtc` far in the past, so they
  cannot be excluded by `StartUtc` alone. The query fetches: rows overlapping the window **OR**
  `Rrule IS NOT NULL` (the recurring set is small and each master expands cheaply for the bounded window).
  A partial/filtered index `IX_Event_Recurring (CalendarId) WHERE Rrule IS NOT NULL` keeps the
  "all masters for these calendars" leg cheap. `IX_Event_Master (MasterId)` (§4) fetches a master's
  overrides for splicing in one indexed lookup.

---

## 4. Key query → index mapping

Exact index definitions for every hot read path. These are the indexes the schema **must** ship with; the
catalog above references them by name.

### 4.1 `GET /events` window — visible calendars × time range (the core read)

The single query behind every view ([API.md](API.md#events-the-projected-stream)):
`calendars ∈ {visible} AND StartUtc < @to AND EndUtc > @from` plus the recurring-master union (§3).

```sql
-- Primary composite: calendar filter + range scan, leftmost-prefix on CalendarId.
CREATE INDEX IX_Event_Calendar_Time ON Event (CalendarId, StartUtc, EndUtc);

-- Recurring masters that may recur INTO the window from an earlier StartUtc (partial index).
CREATE INDEX IX_Event_Recurring ON Event (CalendarId) WHERE Rrule IS NOT NULL;
```

- `CalendarId` leftmost lets the planner pass the explicit visible-calendar subset (`?calendars=ID,ID`) or
  iterate visible calendars; `(StartUtc, EndUtc)` then range-scans the window. This is the dominant query —
  every Month/Week/Agenda/Map render hits it.

### 4.2 Dedup-signature grouping

`ARCHITECTURE §12`: group events sharing a signature **across different calendars**.

```sql
CREATE INDEX IX_Event_DedupSignature ON Event (DedupSignature) WHERE DedupSignature IS NOT NULL;
```

- Groups events by signature in one scan; the engine then collapses cross-calendar matches and writes
  `DuplicateGroupId`/`DuplicateGroup`. `Event.Uid` also indexed (`IX_Event_Uid`) for the birthday/holiday
  `Uid`-keyed signatures and cross-source dedup.

### 4.3 Geocode cache lookup by normalized-query hash

```sql
CREATE UNIQUE INDEX UX_GeocodeCache_QueryHash ON GeocodeCache (QueryHash);
```

- The aggregator normalizes the query → SHA-256 → single-row probe; hit = **zero outbound calls**
  (geo-geocoding deep-dive §3/§7). Unique enforces one row per normalized query (forward) or rounded
  coordinate (reverse), satisfying the mandatory-cache rule and commercial cost control.

### 4.4 `RouteLeg` by `(FromEventId, ToEventId, Mode)`

```sql
CREATE UNIQUE INDEX UX_RouteLeg_From_To_Mode ON RouteLeg (FromEventId, ToEventId, Mode);
```

- The precompute cache + `GET /events/{id}/commute` read this directly (geo-routing deep-dive §4); one leg
  per consecutive pair per mode. A supporting `IX_RouteLeg_FromEventId` (leftmost prefix of the unique
  index) serves "all legs leaving this event" for invalidation when an endpoint moves.

### 4.5 `FareSample` by watch + time

```sql
CREATE INDEX IX_FareSample_Watch_Time ON FareSample (FareWatchId, SampledAtUtc DESC);
```

- `GET /fares/watches/{id}/history` and the multi-month overlay (`GET /fares/overlay`) read **newest-first
  per watch** (travel-fares deep-dive §10); the DESC ordering serves both the chart and the
  "latest price per date" overlay seed without a sort.

### 4.6 Other declared indexes (summary)

| Index | Table (cols) | Serves |
| --- | --- | --- |
| `UX_Calendar_Account_RemoteId` | Calendar (AccountId, RemoteId) | idempotent sync upsert |
| `UX_Event_Calendar_RemoteId` | Event (CalendarId, RemoteId) | idempotent event upsert |
| `IX_Event_Master` | Event (MasterId) WHERE MasterId IS NOT NULL | splice overrides into a master (§3) |
| `IX_Event_PlaceId` | Event (PlaceId) | map pins / route-leg recompute on place move |
| `IX_Category_IsVisible` | Category (IsVisible) | visibility pipeline (§13) |
| `IX_EventCategory_CategoryId` | EventCategory (CategoryId) | "hide all in category" |
| `UX_DuplicateGroup_Signature` | DuplicateGroup (Signature) | group lookup |
| `IX_Place_LatLng` | Place (Lat, Lng) | map bounding-box / region query |
| `UX_Place_NormalizedKey` | Place (NormalizedKey) | place dedupe |
| `IX_Trip_Time` / `IX_ScenarioDraft_Time` | (StartUtc, EndUtc) | `GET /trips`, draft window |
| `IX_FareWatch_IsActive` | FareWatch (IsActive) | scheduler due-watch scan |
| `UX_Share_Token` | Share (Token) | public feed lookup |
| `UX_SyncState_Account_Calendar` | SyncState (AccountId, CalendarId) | per-calendar cursor |
| `IX_WriteOutbox_Status_Enqueued` | WriteOutbox (Status, EnqueuedAtUtc) | FIFO replay worker |

---

## 5. EF Core specifics

Provider: **`Microsoft.EntityFrameworkCore.Sqlite`** (EF Core 9). Configuration is via
`IEntityTypeConfiguration<T>` classes (Fluent API) discovered with
`modelBuilder.ApplyConfigurationsFromAssembly(...)` — no data annotations in the domain entities (the
`Calendar.Domain` project stays POCO/persistence-ignorant; mapping lives in `Calendar.Infrastructure`).

### 5.1 Value converters (registered globally where possible via `ConfigureConventions`)

| Concern | Converter | Notes |
| --- | --- | --- |
| **`DateTimeOffset` ↔ UTC text** | custom `DateTimeOffsetToUtcStringConverter` | normalize to UTC, store ISO-8601 `o`/`u` round-trip text. SQLite has no native datetime; text sorts lexicographically = chronologically for the range scans in §4. Apply to **all** `*Utc` columns via `configurationBuilder.Properties<DateTimeOffset>().HaveConversion<…>()`. |
| **`DateOnly` ↔ text** | `DateOnly` ⇒ `yyyy-MM-dd` text | EF Core 8+ supports `DateOnly` on SQLite; explicit converter pins the format for all-day dates / fare ranges. |
| **enum ↔ string** | `EnumToStringConverter<TEnum>` | per-enum; e.g. `Status`, `Kind`, `Scope`, `Mode`, `TrustTier`. Stored as the member name (stable, readable). |
| **`Guid` ↔ text** | built-in (configure `Properties<Guid>().HaveConversion<GuidToStringConverter>()` for the lower-case `D` format) | enforce TEXT, not BLOB. |
| **`decimal` ↔ text** | EF default on SQLite stores `decimal` as TEXT | keep — avoids `REAL` precision loss for money. |
| **`RowVersion`** | plain `string`, app-stamped | SQLite has **no native `rowversion`/`xmin`**; we re-stamp a new GUID string on every save (see §5.4). Not a converter — a SaveChanges hook. |

### 5.2 JSON columns (flexible bags)

EF Core 9 maps **owned reference types to a single JSON column** via `OwnsOne(...).ToJson()` (and owned
collections via `OwnsMany(...).ToJson()`) — supported on **SQLite** since EF Core 8 and extended in
EF Core 9 (deep owned nesting, LINQ into JSON, change-tracking of JSON contents). We use it for:

- `Plugin.Manifest` / `Plugin.Capabilities`, `PluginConfig.Values` — manifest/config bags.
- `Category.MatchRules` — match-rule set.
- `DuplicateOverride.EventIds`, `Share.Filter`, `TripItem.Details`, `ScenarioDraft.Notes`,
  `FareSample.Detail`, `WriteOutbox.Payload` — open-ended bags.
- The **version vector** when serialized as a map rather than the flat `Lamport`/`DeviceId` columns (we keep
  the flat columns for indexable LWW and reserve a JSON `VersionVector` only if multi-peer vectors are
  needed later).

> Caveat (from EF behavior): a change to **any** property inside a `ToJson()` owned type rewrites the whole
> JSON column (no partial update). That is fine for these write-rarely bags; **never** put a hot-mutated or
> indexed value inside a JSON column — those stay first-class columns (e.g. `Lamport`, `StartUtc`).

For values we want as plain JSON `string`/`JsonDocument` without an owned CLR type, use a
`ValueConverter<T, string>` with `JsonSerializer` + a `ValueComparer` (so change tracking works on the
materialized object).

### 5.3 Owned vs referenced types

| Type | Decision | Why |
| --- | --- | --- |
| **`Place`** | **Referenced (shared) entity** | shared by `Event`/`TripItem`/`FareWatch` and **deduped by proximity+label** so one pin = one route leg = one map cluster (geo-geocoding §3). Owning it would duplicate pins per event and break leg caching. (See §2.5 for the full justification.) |
| `PluginManifest`, `MatchRuleSet`, `ShareFilter`, draft `Notes`, `TripItem.Details` | **Owned, `ToJson()`** | single-owner, schemaless, never queried relationally. |
| `GeoPoint`/coordinate pairs on `Place` | **inline columns** (`Lat`/`Lng`), not owned | needed for the `IX_Place_LatLng` bounding-box index. |
| Sync-metadata quartet | **inline columns** (shadow or explicit) | `Lamport`/`UpdatedAtUtc` must be indexable/queryable for LWW — never JSON. Can be applied uniformly via a base-type configuration or a model-building convention over an `ISyncEntity` marker interface. |

### 5.4 Concurrency tokens

- **Local:** a `RowVersion` string property marked `.IsConcurrencyToken()` and re-stamped in
  `SaveChanges`/`SaveChangesAsync` via an interceptor (`SaveChangesInterceptor`) or
  `ChangeTracker.Entries()` loop — because SQLite has no auto-`rowversion`. A mid-air collision throws
  `DbUpdateConcurrencyException`, surfaced as RFC-9457 `409` by the API.
- **Upstream (provider):** `Event.ETag` / `SyncState.SyncToken` drive `If-Match`/`412` write-back
  (CalDAV/Google deep-dives §9/§11) — orthogonal to the local token.
- The REST `ETag`/`If-Match` ([API.md](API.md#conventions)) is computed from the entity `RowVersion`.

### 5.5 SQLCipher (at-rest encryption of the local DB)

The on-device SQLite file is encrypted with **SQLCipher** (ARCHITECTURE §2: "EF Core + SQLite
(SQLCipher-capable)"; §17 privacy):

- Use **`Microsoft.Data.Sqlite`** with the **SQLCipher**-enabled SQLite native bundle
  (`SQLitePCLRaw.bundle_e_sqlcipher`), or open the connection and issue `PRAGMA key = '<derived-key>';`
  (and `PRAGMA cipher_*` tuning) before the first query — typically via a `DbConnectionInterceptor` or a
  custom `DbConnection` factory so EF's pooled connections are keyed on open.
- The DB key is **derived** (Argon2id, per ARCHITECTURE §8) from the user passphrase / OS keystore-protected
  secret; it is **not** stored in the DB. The cloud-sync passphrase (`POST /cloud/enable`) derives the E2E
  key client-side separately.
- **Defence in depth:** `SecretRef` rows hold AEAD ciphertext **inside** the already-encrypted SQLCipher
  file, so a stolen key for one layer does not expose tokens.

---

## 6. C# sketches

Illustrative anchors only (not a compiled project). Entities are POCOs in `Calendar.Domain`; configuration
lives in `Calendar.Infrastructure`.

### 6.1 `Event` (master + recurrence override)

```csharp
public sealed class Event
{
    public Guid Id { get; set; }
    public Guid CalendarId { get; set; }

    public string Uid { get; set; } = default!;          // iCal UID / iCalUID — dedup key
    public string RemoteId { get; set; } = default!;     // Google id / CalDAV href / (UID,RECURRENCE-ID)

    public string? Title { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public bool AllDay { get; set; }
    public DateOnly? StartDate { get; set; }             // set only when AllDay (floating, no zone)

    public string? Rrule { get; set; }                   // master rule; null on overrides/singletons
    public Guid? MasterId { get; set; }                  // self-FK on override rows
    public DateTimeOffset? RecurrenceId { get; set; }    // which occurrence an override replaces

    public Guid? PlaceId { get; set; }
    public string? Location { get; set; }

    public string? DedupSignature { get; set; }
    public Guid? DuplicateGroupId { get; set; }

    public EventStatus Status { get; set; }              // Confirmed | Tentative | Cancelled
    public string? ETag { get; set; }                    // provider concurrency
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public string RowVersion { get; set; } = default!;   // local concurrency (app-stamped)

    public Calendar Calendar { get; set; } = default!;
    public Place? Place { get; set; }
    public Event? Master { get; set; }
    public ICollection<Event> Overrides { get; set; } = new List<Event>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
}

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> b)
    {
        b.ToTable("Event");
        b.HasKey(e => e.Id);

        b.Property(e => e.Status).HasConversion<string>();           // enum → text
        b.Property(e => e.RowVersion).IsConcurrencyToken();          // app re-stamps in SaveChanges

        b.HasOne(e => e.Calendar).WithMany()
            .HasForeignKey(e => e.CalendarId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(e => e.Place).WithMany()
            .HasForeignKey(e => e.PlaceId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne(e => e.Master).WithMany(e => e.Overrides)
            .HasForeignKey(e => e.MasterId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(e => e.Categories).WithMany()
            .UsingEntity("EventCategory");                            // join table (see §2.3)

        b.HasIndex(e => new { e.CalendarId, e.StartUtc, e.EndUtc })
            .HasDatabaseName("IX_Event_Calendar_Time");
        b.HasIndex(e => new { e.CalendarId, e.RemoteId })
            .IsUnique().HasDatabaseName("UX_Event_Calendar_RemoteId");
        b.HasIndex(e => e.DedupSignature)
            .HasDatabaseName("IX_Event_DedupSignature")
            .HasFilter("\"DedupSignature\" IS NOT NULL");
        b.HasIndex(e => e.MasterId)
            .HasDatabaseName("IX_Event_Master")
            .HasFilter("\"MasterId\" IS NOT NULL");
        b.HasIndex(e => e.Uid).HasDatabaseName("IX_Event_Uid");
        b.HasIndex(e => e.PlaceId).HasDatabaseName("IX_Event_PlaceId");
    }
}
```

### 6.2 `Account` (sync-metadata + vault handle)

```csharp
public sealed class Account : ISyncEntity   // marker: gets the sync-metadata quartet
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public Guid? AuthRef { get; set; }              // → SecretRef.Id (token vault)
    public int Priority { get; set; }               // dedup canonical ordering
    public AccountStatus Status { get; set; }
    public DateTimeOffset? LastSyncAtUtc { get; set; }

    // ISyncEntity (applied via convention in OnModelCreating)
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid DeviceId { get; set; }
    public long Lamport { get; set; }
    public bool IsDeleted { get; set; }
}

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("Account");
        b.HasKey(a => a.Id);
        b.Property(a => a.Status).HasConversion<string>();

        b.HasOne<Plugin>().WithMany()
            .HasForeignKey(a => a.PluginId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SecretRef>().WithMany()
            .HasForeignKey(a => a.AuthRef).OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(a => a.PluginId).HasDatabaseName("IX_Account_PluginId");
        b.HasIndex(a => a.Priority).HasDatabaseName("IX_Account_Priority");
        // sync-metadata columns + global query filter (!IsDeleted) applied by SyncEntityConvention.
    }
}
```

### 6.3 `RouteLeg` (cache keyed by from/to/mode)

```csharp
public sealed class RouteLeg
{
    public Guid Id { get; set; }
    public Guid FromEventId { get; set; }
    public Guid ToEventId { get; set; }
    public TravelMode Mode { get; set; }            // Drive | Transit | Walk | Bike
    public int DurationSec { get; set; }
    public DateTimeOffset? LeaveByUtc { get; set; } // host-computed
    public bool Feasible { get; set; }              // host-computed
    public string? Geometry { get; set; }           // polyline precision 5
    public string Source { get; set; } = default!;  // winning provider id
    public DateTimeOffset ComputedAtUtc { get; set; }
    public bool IsStale { get; set; }
}

public sealed class RouteLegConfiguration : IEntityTypeConfiguration<RouteLeg>
{
    public void Configure(EntityTypeBuilder<RouteLeg> b)
    {
        b.ToTable("RouteLeg");
        b.HasKey(r => r.Id);
        b.Property(r => r.Mode).HasConversion<string>();

        b.HasOne<Event>().WithMany()
            .HasForeignKey(r => r.FromEventId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Event>().WithMany()
            .HasForeignKey(r => r.ToEventId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(r => new { r.FromEventId, r.ToEventId, r.Mode })
            .IsUnique().HasDatabaseName("UX_RouteLeg_From_To_Mode");
    }
}
```

> A `SyncEntityConvention` (model-building convention over `ISyncEntity`) adds `UpdatedAtUtc`, `DeviceId`,
> `Lamport`, `IsDeleted` and a `HasQueryFilter(e => !e.IsDeleted)` to every user-metadata entity uniformly,
> so the four columns and tombstone filter aren't hand-repeated per configuration.

---

## 7. Migration strategy

- **Tooling:** EF Core **migrations** (`dotnet ef migrations add`), one migration history table
  (`__EFMigrationsHistory`) in the SQLite/SQLCipher DB. Migrations are applied at app startup via
  `db.Database.Migrate()` (single-user local host) after the SQLCipher key is set on the connection.
- **Naming:** timestamped, intent-revealing PascalCase — `20260601_InitialSchema`,
  `20260710_AddFareSampleHistory`, `20260815_AddScenarioDraft`. The initial migration creates the full
  catalog in §2 with all §4 indexes.
- **SQLite migration caveats:** SQLite cannot `ALTER COLUMN`/`DROP COLUMN` in older engines; EF Core's
  SQLite provider emits the **table-rebuild** pattern (create new, copy, drop, rename) automatically — keep
  migrations small and review generated SQL for large tables. Prefer additive changes; backfill data in a
  follow-up migration step or a one-time hosted service.
- **Seed data** (via `HasData` in configurations, or an idempotent startup seeder for rows that reference
  generated keys):
  - **Built-in categories** (`IsBuiltIn = true`, stable well-known GUIDs): `Work`, `Personal`, `Birthday`,
    `Holiday`, `Travel`, `Busy`. Their `MatchRules` seed the source-derived rules (Google Birthdays/Holidays
    calendars, CalDAV `CATEGORIES`, Graph categories, ICS `CATEGORIES`, TripIt-forced `Travel`).
  - **Built-in (first-party) plugins** registered as `Plugin` rows with `TrustTier = InBox` and `Status =
    Installed`: the calendar plugins (`org.unifiedcalendar.google`, `…microsoft`, `…caldav`,
    `org.unifiedcalendar.ics`), geo (`…geo.nominatim`, `…geo.photon`, `…osrm`, MapLibre tiles), and travel
    (`com.duffel.flights`, `com.duffel.stays`, `com.kiwi.tequila`) — exactly the ids used in the deep-dive
    manifests. These are discovered at runtime (ARCHITECTURE §4), so the seed is a convenience registry, not
    a hardcode; manifests remain the source of truth.
  - **Local `DeviceId`** for this install (a single-row `Device`/settings entry) generated on first run and
    used to stamp the sync-metadata quartet.
- **Cloud-node schema** (optional PostgreSQL relay, ARCHITECTURE §2/§8) is **separate** and out of scope
  here — it stores only ciphertext blobs + version vectors + share metadata, never these tables in cleartext.

---

## 8. ER diagram

Refines [ARCHITECTURE §9](ARCHITECTURE.md#9-domain-model) with the new tables (`Plugin`/`PluginConfig`,
`SecretRef`, `EventCategory`, `DuplicateOverride`, `FareSample`, `ScenarioDraft`, `SyncState`,
`WriteOutbox`).

```mermaid
erDiagram
    PLUGIN ||--o{ PLUGINCONFIG : configures
    PLUGIN ||--o{ ACCOUNT : backs
    ACCOUNT ||--o| SECRETREF : "auth via"
    ACCOUNT ||--o{ CALENDAR : has
    ACCOUNT ||--o{ SYNCSTATE : "cursor per (acct,cal)"
    CALENDAR ||--o{ EVENT : contains
    CALENDAR ||--o{ SHARE : "published as"

    EVENT ||--o{ EVENTCATEGORY : tagged
    CATEGORY ||--o{ EVENTCATEGORY : tags
    EVENT }o--o| DUPLICATEGROUP : "member of"
    DUPLICATEGROUP }o--o| EVENT : canonical
    DUPLICATEOVERRIDE }o--o{ EVENT : "merge/split (by Uid)"
    EVENT ||--o{ EVENT : "master → overrides"

    EVENT }o--o| PLACE : "located at"
    PLACE ||--o{ GEOCODECACHE : resolved
    EVENT ||--o{ ROUTELEG : "commute (from/to)"

    TRIP ||--o{ TRIPITEM : groups
    TRIP ||--o{ SCENARIODRAFT : drafts
    TRIPITEM }o--o| PLACE : at
    TRIPITEM }o--o| EVENT : "projects to"
    FAREWATCH }o--o| PLACE : "origin/dest"
    FAREWATCH ||--o{ FARESAMPLE : "price history"

    EVENT ||--o{ WRITEOUTBOX : "queued writes"

    PLUGIN { string Id PK; string Kind; string Status; json Manifest; string TrustTier }
    PLUGINCONFIG { guid Id PK; string PluginId FK; json Values }
    ACCOUNT { guid Id PK; string PluginId FK; guid AuthRef FK; int Priority; string Status }
    SECRETREF { guid Id PK; string Kind; string KeyId; blob Ciphertext; blob Nonce }
    CALENDAR { guid Id PK; guid AccountId FK; string RemoteId; bool IsVisible; bool IsReadOnly }
    EVENT { guid Id PK; guid CalendarId FK; string Uid; string RemoteId; datetime StartUtc; datetime EndUtc; bool AllDay; string Rrule; guid MasterId FK; datetime RecurrenceId; guid PlaceId FK; string DedupSignature; guid DuplicateGroupId FK; string Status; string ETag }
    CATEGORY { guid Id PK; string Name; bool IsVisible; json MatchRules; bool IsBuiltIn }
    EVENTCATEGORY { guid EventId PK_FK; guid CategoryId PK_FK; string AssignedBy }
    DUPLICATEGROUP { guid Id PK; string Signature; guid CanonicalEventId FK }
    DUPLICATEOVERRIDE { guid Id PK; string Kind; json EventIds }
    PLACE { guid Id PK; string Label; double Lat; double Lng; string Address; string NormalizedKey }
    GEOCODECACHE { guid Id PK; string Query; string QueryHash; double Lat; double Lng; string Source; bool IsReverse }
    ROUTELEG { guid Id PK; guid FromEventId FK; guid ToEventId FK; string Mode; int DurationSec; datetime LeaveByUtc; bool Feasible; string Source }
    TRIP { guid Id PK; string Name; datetime StartUtc; datetime EndUtc }
    TRIPITEM { guid Id PK; guid TripId FK; string Kind; string Status; guid PlaceId FK; guid ProjectedEventId FK; string Confirmation }
    SCENARIODRAFT { guid Id PK; guid TripId FK; string Title; datetime StartUtc; datetime EndUtc; guid PlaceId FK; string Kind }
    FAREWATCH { guid Id PK; string Kind; guid OriginPlaceId FK; guid DestPlaceId FK; date RangeStart; date RangeEnd; int Pax; bool IsActive }
    FARESAMPLE { guid Id PK; guid FareWatchId FK; datetime SampledAtUtc; decimal Price; string Currency; string Source; bool IsStale }
    SHARE { guid Id PK; guid CalendarId FK; json Filter; string Token; string Scope; datetime ExpiresAtUtc }
    SYNCSTATE { guid Id PK; guid AccountId FK; guid CalendarId FK; string SyncToken; string Ctag; string State; datetime NextRunAtUtc }
    WRITEOUTBOX { guid Id PK; string EntityType; guid EntityId; string Operation; json Payload; string BaseETag; string Status; int Attempts }
```

---

## Sources

EF Core 9 / SQLite specifics cited in §5 (the rest of the schema derives from the in-repo
[ARCHITECTURE.md](ARCHITECTURE.md), [API.md](API.md), [PLUGINS.md](PLUGINS.md), and the
[deep dives](deep-dives/)):

- EF Core 8 — JSON column mapping extended to SQLite; owned types `ToJson()`: <https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-8.0/whatsnew>
- EF Core 7 — original JSON column / owned-type-to-JSON support and value converters: <https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-7.0/whatsnew>
- EF Core 9 — deeper JSON integration (nested owned types, LINQ into JSON, change tracking): <https://gunesramazan.medium.com/deep-json-integration-new-capabilities-in-ef-core-9-a7e288983987>
- SQLite `jsonb` tracking issue (native JSON storage status in EF/SQLite): <https://github.com/dotnet/efcore/issues/34073>
