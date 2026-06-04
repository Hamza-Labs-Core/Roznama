# 0005 — Two plugin kinds: declarative connectors preferred; assembly plugins for the rest

**Status:** Accepted (2026-06)

## Context

Most integrations are "call REST endpoints, map JSON to the domain model" (fares, geocoding, routing,
many calendars). A few are not: CalDAV's WebDAV/XML `REPORT`, Microsoft Graph's stateful delta, ICS's
iCalendar parsing, sync tokens. Requiring compiled code for every integration raises the bar and the
attack surface; forcing everything into data can't express non-REST protocols
([PLUGINS.md §2 The two plugin kinds](../PLUGINS.md#2-the-two-plugin-kinds),
[§4 Declarative connectors](../PLUGINS.md#4-declarative-connectors-read-apis--load-them),
[SDK-CONTRACT.md §8](../SDK-CONTRACT.md#8-declarative-connectors-satisfy-the-same-interfaces)).

## Decision

Support **two plugin kinds, both satisfying the same capability interfaces**:

- **Declarative connectors (preferred default):** a manifest + OpenAPI spec + JSONata/JMESPath field
  mapping — *no code*. Run in the shared **connector engine**, which the host surfaces to the registry
  as an ordinary `IPlugin`. The core cannot tell the two kinds apart.
- **Assembly plugins:** compiled C# against `Calendar.Plugin.Abstractions`, for protocols/logic that
  aren't "call endpoints, map JSON" (CalDAV, Graph delta, ICS, an EventKit bridge).

## Consequences

- **Positive:** most providers ship as data — no compilation, no deploy, lower trust risk
  ([0007](0007-alc-isolation-out-of-process.md)); the connector engine centralizes auth, pagination,
  retry, egress filtering, and schema validation; identical registry semantics for both kinds.
- **Negative / trade-off:** the connector engine is a non-trivial host component (OpenAPI reader,
  mapping evaluator, pagination/auth binding) that must be built and hardened; expressive but capped —
  odd auth, GraphQL, XML/CalDAV force an assembly plugin.

## Alternatives considered

- **Assembly plugins only** — uniform but makes every trivial REST integration a signed DLL; rejected
  for friction and attack surface.
- **Declarative only** — can't express CalDAV/Graph/ICS; rejected.
