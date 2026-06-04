# 0011 — TripIt and Proton integrated via the ICS plugin

**Status:** Accepted (2026-06)

## Context

Two desirable sources have no usable programmatic API (as of June 2026):

- **TripIt** — the best itinerary aggregator, but its **public API is closed to new integrations**;
  new OAuth/API access is effectively unavailable (partnership-only).
- **Proton Calendar** — end-to-end encrypted by design, so it **does not and will not support CalDAV**,
  and has no public API.

Both, however, expose a **read-only `.ics` feed/share link**
([PLUGIN-RESEARCH.md §Proton](../PLUGIN-RESEARCH.md#proton--calendarread----workaround-only),
[§TripIt](../PLUGIN-RESEARCH.md#tripit--itineraryimport----use-the-feed),
[ARCHITECTURE.md §11](../ARCHITECTURE.md#11-calendar-capabilities--iosicloud),
[§14](../ARCHITECTURE.md#14-travel-itineraries-stays--fares)).

## Decision

Integrate **both TripIt and Proton through the existing ICS plugin**, not dedicated API plugins.
TripIt's read-only Calendar Feed `.ics` is consumed and its items tagged `Travel` (itinerary import
*expressed through* `calendar.read`), projecting flights/hotels/cars/rail into events. Proton's
"Share via link → Create link" ICS URL is subscribed via the same ICS plugin (Full view = details,
Limited view = busy-only). A true `IItinerarySource`/Proton plugin is built **only if** official API/
partner access later appears — the SDK interface stays canonical so such a plugin slots in unchanged.

## Consequences

- **Positive:** unblocks two valuable sources with zero new auth work, reusing the Phase 1 ICS plugin;
  TripIt items ride dedup/visibility/multi-month/map automatically; no dependency on closed APIs.
- **Negative / trade-off:** read-only — no write-back; freshness is bound by the feed's refresh cadence
  (TripIt ~15 min–24 h) and a *downloaded* (vs subscribed) Proton ICS won't reflect later edits; relies
  on the providers continuing to offer their ICS feeds.

## Alternatives considered

- **TripIt official API plugin** — ideal but unavailable to new integrations; deferred behind a partner-
  access condition.
- **Proton CalDAV plugin** — impossible by Proton's E2E design; rejected.
- **Skip these providers** — leaves common real-world accounts unsupported when an ICS path exists;
  rejected.
