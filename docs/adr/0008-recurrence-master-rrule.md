# 0008 — Recurrence stored as master + RRULE, expanded on demand

**Status:** Accepted (2026-06)

## Context

Recurring events can span years (a 10-year weekly event = ~520 instances). Materializing every
occurrence bloats storage, complicates sync (the provider hands back masters + RRULE, not instances),
and the UI only ever needs the visible range
([DATA-SCHEMA.md §3 Recurrence model](../DATA-SCHEMA.md#3-recurrence-model),
[ARCHITECTURE.md §9](../ARCHITECTURE.md#9-domain-model), [UI.md §9](../UI.md#9-performance)). Google,
CalDAV, and ICS all deliver master + rule, so the storage model should mirror that.

## Decision

Store recurrence as a **master `Event` row** (`Rrule` set, `MasterId`/`RecurrenceId` null) plus separate
**override rows** for moved/edited instances (`MasterId` → master, `RecurrenceId` = original start,
`Rrule` null). **Occurrences are never materialized.** The projection service expands `[from, to)` on
demand for the visible window with **Ical.Net** (`GetOccurrences`), splicing in overrides and applying
`EXDATE`/`RDATE`. A composite index `IX_Event_Calendar_Time (CalendarId, StartUtc, EndUtc)` plus a
partial `IX_Event_Recurring … WHERE Rrule IS NOT NULL` drive the windowed read; switching views
re-projects from cached masters rather than re-querying the provider.

## Consequences

- **Positive:** a recurring series is one row, not hundreds; sync stays idempotent and mirrors provider
  shape; view switching is instant; storage and the hot read path stay small.
- **Negative / trade-off:** every read path must run expansion logic (Ical.Net) rather than a plain row
  scan; masters can recur into a window from a far-past `StartUtc`, so the time-range query needs the
  recurring-master union (a partial index) and can't prune purely by `StartUtc`; correctness leans on
  one expansion library.

## Alternatives considered

- **Materialize occurrences into rows** — trivial range queries, but storage blowup, painful infinite/
  long series, and harder edit-one-instance + sync reconciliation; rejected.
- **Expand fully in memory at load** — wasteful for long series the user never scrolls to; on-demand
  windowed expansion is strictly cheaper.
