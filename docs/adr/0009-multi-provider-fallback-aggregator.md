# 0009 — Multi-provider fallback aggregator for interchangeable capabilities

**Status:** Accepted (2026-06)

## Context

Some capabilities return **interchangeable results for the same query** — `flight.price`, `stay.price`,
`geo.route`, `geo.geocode` — and their providers are rate-limited, paid, quota-bound, or occasionally
down. A single hard-wired provider per capability means one outage or 429 breaks a core feature
([ARCHITECTURE.md §15 Multi-provider fallback aggregator](../ARCHITECTURE.md#15-multi-provider-fallback-aggregator),
[§5](../ARCHITECTURE.md#5-capability-catalog),
[SDK-CONTRACT.md §2.4](../SDK-CONTRACT.md#24-capability-id-catalog)).

## Decision

Front every interchangeable-result capability with a generic **fallback aggregator**. It supports
**failover** (first good result — cheap default) and **fan-out + merge** (normalize, dedupe identical
results, pick best, tag the winning `Source` for deep-link/attribution). A **routing policy** picks the
primary by coverage/health/cost/remaining quota — not a fixed order — and per-plugin Polly circuit
breakers, rate budgets, and timeouts apply. On total failure it returns the **last-known stored result
flagged `stale`**. `geo.tiles` is explicitly **not** aggregated — a basemap is a single chosen surface
that falls back to a bundled style, not to "the next provider".

## Consequences

- **Positive:** one dead/slow/throttled provider never breaks a query; cost/quota-aware routing across
  free self-hosted and paid providers ([0010](0010-travel-pricing-duffel.md),
  [0012](0012-self-hosted-geo-default.md)); built-in observability (which plugins tried, hit/miss,
  latency, winner); graceful degradation to history.
- **Negative / trade-off:** fan-out + merge needs per-capability dedupe/"best" rules (e.g. flights key on
  `Carrier+FlightNo+DepartUtc+ArriveUtc`); stale results must be clearly flagged to avoid acting on old
  prices; the routing policy and budget accounting add host complexity.

## Alternatives considered

- **One fixed provider per capability** — simplest, but no resilience and no cost/quota balancing;
  rejected.
- **Aggregate everything, including tiles** — wrong for `geo.tiles` (a basemap isn't an interchangeable
  query result); explicitly excluded.
