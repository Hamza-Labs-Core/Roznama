# Code audit backlog — 2026-06-10

A 7-angle review (line-scan, weakened-invariant, cross-file contracts, reuse, simplification,
efficiency, altitude) over the full `phase-0-foundations` implementation. **Confirmed correctness bugs
were fixed in the same audit** (enum wire serialization, cloud-sync wedges & double-apply, dedup
override edge cases, all-day import shift, ICS connect validation, OAuth pending TTL, SSE gaps,
outbox DTO drift). What follows is the verified-but-deferred backlog, ranked.

## Security / robustness

1. **Marketplace trust gates don't apply at startup load.** All signature/integrity gates live in
   `MarketplaceService.InstallAsync` (the download path). `PluginHost.LoadAllAsync` loads and runs
   every bundle directory it finds, and the persisted `Plugin.TrustTier` is never consulted at load —
   so anything that can write a directory under the plugin root gets in-proc execution at next
   startup, and a LocalDev install is indistinguishable from InBox after a restart. Fix at the load
   altitude: record a per-bundle digest+tier at install, verify at load, treat unknown dirs as
   local-dev (refused unless opted in). Pairs with the deferred out-of-process work (ADR-0007 §8.4).

## Efficiency (visible at a few thousand events)

2. **`CalendarSyncService` N+1 upserts.** One `FirstOrDefaultAsync` per remote event + per-calendar
   saves: a 5k-event first sync is ~5k sequential queries. Batch-load the calendar's events keyed by
   `RemoteId` (one query), upsert against the map, save once per calendar.
   `LinkOverridesToMastersAsync` similarly re-queries unlinkable overrides on every sync — fetch all
   candidate masters in one `Contains` query.
3. **`DedupGrouper` full-table regroup.** Loads every signatured event as tracked entities after every
   sync and override change. Scope the query: signatures appearing in ≥2 distinct calendars (GROUP BY
   … HAVING) plus override-listed UIDs; only write rows whose group actually changed.
4. **`EventProjectionService` loads all recurring masters** (plus the whole `DuplicateGroups` table)
   per request regardless of window. Bound masters by start/UNTIL horizon; fetch only referenced groups.
5. **`Home.razor` search has no debounce/sequencing** (a request per keystroke; a slow early response
   can overwrite a newer one) and **SSE reloads don't coalesce** (a sweep over N accounts → N full
   reloads). Debounce ~250 ms with a per-input CTS; coalesce SSE bursts with a short timer.
6. **Cloud pull row-by-row lookups.** Per-item `FirstOrDefaultAsync` (now softened by the per-blob
   batching); batch-load locals per type with one `Contains` query and apply LWW in memory.

## Reuse / consolidation

7. **`EnsurePluginRowAsync` ×3 + `UpsertPluginRowAsync`** (AccountService, AccountConnectService,
   CloudSyncService, MarketplaceService) have already diverged on defaults (Version `1.0.0` vs
   `0.0.0`, Capabilities `["calendar.read"]` vs empty). Extract one `PluginRowSeeder`.
8. **The sync-quartet stamp** (`UpdatedAtUtc/DeviceId/Lamport++`) exists as 2 private helpers + ~6
   inline copies. It is the LWW correctness invariant — promote to one `ISyncEntity` extension.
9. **Four identical hosted pollers** (FareWatch, CalendarSync, ReminderSweep, CloudSync) with already-
   drifted logging. Extract a `PeriodicSweepService<TService>` base; cross-cutting needs (jitter,
   drain-on-shutdown, health surface) then land once.
10. **`Fingerprint`/`Round` duplicated** (RouteService, TripRouteService) — shared `PlaceFingerprint`
    helper; it's a persisted cache key, so the copies must never diverge.
11. **Error-body shapes split** between `{ error }` objects and RFC7807 `Problem` bodies (concretely:
    409s differ between plugin uninstall and write conflicts). Standardize one helper.
12. **Test-infra copies**: `InMemoryTokenVault` ×3, stub `HttpMessageHandler` ×12 across test
    projects. One shared test-support source.

## Simplification / structure

13. **`Home.razor` (~620 lines)**: extract the account-connect cluster (~170 lines, ten fields) and
    the toolbar search into components; every keystroke currently re-renders the whole shell.
14. **`Program.cs` (~1000 lines)**: group endpoints into MapXxx extension classes; collapse the
    write-endpoint catch ladders with one exception-mapping helper.
15. **`CloudSyncService.CollectAsync` observer-callback ceremony** — return the max cursor instead of
    threading an `Action<DateTimeOffset>`; consider a type-metadata registry so push/apply iterate one
    list (adding a synced type is currently three coordinated edits with no compile-time signal).
16. **`SyncScheduler` failure-path copy-back block** writes to a detached local the caller never
    reads — delete it; unify the success/failure bookkeeping epilogue.
17. **`SecretKind.FeedUrl` doubles as "account config blob"** for every plugin kind. Introduce
    `SecretKind.AccountConfig` (one-line enum + data migration) before more rows accumulate, or move
    non-secret config out of the vault entirely.
18. **ICS connect still has a parallel legacy path** (`AccountService.ConnectIcsAsync` + endpoint
    special case) beside the generic scheme-none connect. Fold feedUrl into the generic config dict
    and retire the legacy path (behavior note: legacy 502s on first-sync failure after creating the
    account; generic is best-effort + scheduler retry — pick the generic semantics).
