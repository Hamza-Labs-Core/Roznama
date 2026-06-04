# Architecture Decision Records

This folder captures the significant architecture decisions for **Unified Calendar** (.NET 9 / C#).
Each decision was already made and documented across the design docs (`ARCHITECTURE.md`, `PLUGINS.md`,
`SDK-CONTRACT.md`, `PLUGIN-RESEARCH.md`, `DATA-SCHEMA.md`, `UI.md`, `ROADMAP.md`); these ADRs distill
the *why* so the rationale survives.

**Format.** MADR-style, one decision per file, kept short (~half a page):
**Status · Context · Decision · Consequences · Alternatives considered**. Each ADR cites the source
doc/section it captures. Files are named `NNNN-kebab-title.md` (zero-padded, starting `0001`); numbers
are immutable once assigned. A superseded decision keeps its file and is marked `Superseded by NNNN`.

## Index

| # | Title | Status |
| --- | --- | --- |
| [0001](0001-dotnet-9-single-stack.md) | .NET 9 / C# as the single stack (host + plugins + UI) | Accepted (2026-06) |
| [0002](0002-blazor-wasm-pwa-frontend.md) | Blazor WebAssembly (PWA) for the front end | Accepted (2026-06) |
| [0003](0003-local-first-sqlite-e2e-sync.md) | Hybrid local-first: SQLite source of truth + optional zero-knowledge cloud sync | Accepted (2026-06) |
| [0004](0004-plugin-capability-architecture.md) | Plugin + capability architecture: nothing hardcoded | Accepted (2026-06) |
| [0005](0005-two-plugin-kinds.md) | Two plugin kinds: declarative connectors preferred; assembly plugins for the rest | Accepted (2026-06) |
| [0006](0006-host-owns-secrets-auth-broker.md) | Host owns all secrets via an auth broker | Accepted (2026-06) |
| [0007](0007-alc-isolation-out-of-process.md) | AssemblyLoadContext isolation + optional out-of-process (gRPC) | Accepted (2026-06) |
| [0008](0008-recurrence-master-rrule.md) | Recurrence stored as master + RRULE, expanded on demand | Accepted (2026-06) |
| [0009](0009-multi-provider-fallback-aggregator.md) | Multi-provider fallback aggregator for interchangeable capabilities | Accepted (2026-06) |
| [0010](0010-travel-pricing-duffel.md) | Travel pricing: lead with Duffel (+ Kiwi fallback); exclude Amadeus | Accepted (2026-06) |
| [0011](0011-tripit-proton-via-ics.md) | TripIt and Proton integrated via the ICS plugin | Accepted (2026-06) |
| [0012](0012-self-hosted-geo-default.md) | Self-hosted geo by default; commercial as optional keyed plugins | Accepted (2026-06) |
| [0013](0013-maplibre-and-routes-api.md) | Maps via MapLibre GL; routing via Google Routes API v2 | Accepted (2026-06) |
