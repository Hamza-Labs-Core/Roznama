# 0004 — Plugin + capability architecture: nothing hardcoded

**Status:** Accepted (2026-06)

## Context

The product aggregates calendars, travel, fares, tiles, geocoding, routing, and notifications from an
open-ended set of providers. Baking providers into the core would mean editing core code for every new
integration and would couple the UI to specific services
([ARCHITECTURE.md §1, §4 Plugin & capability system](../ARCHITECTURE.md#4-plugin--capability-system),
[PLUGINS.md §1 Concepts](../PLUGINS.md#1-concepts)).

## Decision

The core knows **capabilities, never providers**. A **capability** is a named contract
(`calendar.read`, `flight.price`, `geo.route`, …; full catalog in
[ARCHITECTURE.md §5](../ARCHITECTURE.md#5-capability-catalog) /
[SDK-CONTRACT.md §2.4](../SDK-CONTRACT.md#24-capability-id-catalog)). A **plugin** declares a manifest and
implements one or more capabilities. A **registry** maps `capability → [plugins]`; the domain layer only
ever asks "who provides X" and never references a provider by name. Built-in providers (Google, CalDAV,
ICS, MapLibre, …) ship as **first-party plugins** exercising the exact same SDK as third parties. Adding
a capability = adding an interface to the SDK; existing plugins are unaffected.

## Consequences

- **Positive:** new integrations never touch the core; the UI is provider-agnostic too (schema-driven
  config, [PLUGINS.md §9](../PLUGINS.md#9-schema-driven-configuration-ui)); first-party plugins keep the
  SDK honest by dogfooding it; capabilities compose cleanly with the aggregator
  ([0009](0009-multi-provider-fallback-aggregator.md)).
- **Negative / trade-off:** an indirection layer (registry, manifest, lifecycle) the core must carry
  before the first feature works — the roadmap accepts this by building the host in Phase 0
  ([ROADMAP.md Phase 0](../ROADMAP.md)); the SDK contract becomes a hard compatibility surface to
  maintain ([SDK-CONTRACT.md §1](../SDK-CONTRACT.md#1-versioning--compatibility-policy)).

## Alternatives considered

- **Hardcoded providers behind internal interfaces** — simpler initially, but every integration becomes
  a core change and the UI grows provider-specific code; rejected as the antithesis of the design.
- **Plugins keyed by provider name (not capability)** — loses the interchangeability that powers the
  fallback aggregator; rejected.
