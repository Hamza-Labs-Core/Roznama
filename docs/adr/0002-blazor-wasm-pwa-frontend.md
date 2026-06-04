# 0002 — Blazor WebAssembly (PWA) for the front end

**Status:** Accepted (2026-06)

## Context

The UI *is* the product: a fast, offline-capable planning canvas with multiple views over one
deduped, recurrence-expanded event stream, where switching views never re-queries providers
([UI.md §1, §3](../UI.md#1-goals), [ARCHITECTURE.md §1 Design principles](../ARCHITECTURE.md#1-design-principles)).
Two non-negotiables: it must **open instantly and work offline** (calendar + map on a plane), and it
should share domain types with the rest of the stack ([0001](0001-dotnet-9-single-stack.md)). The map
view needs MapLibre GL JS, so JS interop is required regardless of framework.

## Decision

Build the front end as a **Blazor WebAssembly app, installable as a PWA**
([ARCHITECTURE.md §2](../ARCHITECTURE.md#2-technology-stack), [UI.md §10](../UI.md#10-accessibility-theming--offline)).
A service worker caches the app shell + last projection; the projected event stream is cached in
IndexedDB for instant cold start and offline view ([UI.md §9](../UI.md#9-performance)). MapLibre GL JS
is driven via JS interop.

## Consequences

- **Positive:** C# and the domain model run client-side — no DTO duplication across a JS boundary;
  PWA + IndexedDB delivers the offline/instant requirement; on-demand recurrence expansion and
  virtualized rendering run in the same language as the host.
- **Negative / trade-off:** WASM download size and cold-start/runtime cost are higher than a hand-tuned
  JS SPA; map and any other JS libraries still require interop shims; Blazor WASM has a smaller
  component ecosystem than React.

## Alternatives considered

- **React / other JS SPA** — largest ecosystem and smallest payloads, but reintroduces a second
  language and a duplicated domain model across the API boundary, against [0001](0001-dotnet-9-single-stack.md).
- **Blazor Server** — tiny payload, but needs a live socket and breaks the offline-on-a-plane
  requirement; rejected.
