# 0010 — Travel pricing: lead with Duffel (+ Kiwi fallback); exclude Amadeus

**Status:** Accepted (2026-06)

## Context

`flight.price` / `stay.price` need a primary provider with a modern REST API, broad coverage, and ideally
an official C# client. The obvious incumbent, **Amadeus Self-Service, is being decommissioned on
2026-07-17** — building the starter plugin on it is a dead end (Enterprise APIs remain but are
heavyweight/contracted)
([PLUGIN-RESEARCH.md §Phase 5 Travel](../PLUGIN-RESEARCH.md#phase-5--travel),
[ARCHITECTURE.md §14](../ARCHITECTURE.md#14-travel-itineraries-stays--fares),
[ROADMAP.md Phase 5](../ROADMAP.md)).

## Decision

**Lead with Duffel** for both flights and stays — modern REST, search **and** book across 300+ airlines,
Duffel Stays for hotels, and an official **C# SDK**. Add **Kiwi (Tequila)** as a second source behind the
fallback aggregator ([0009](0009-multi-provider-fallback-aggregator.md)). **Do not build on Amadeus
Self-Service**; reconsider Amadeus only if Enterprise access is acquired. Hotelbeds/Expedia Rapid/Booking
Demand and Travelpayouts remain partner/affiliate alternatives. **Booking stays external** (deep-link/
affiliate) unless Duffel ticketing is adopted, since being an OTA is regulated.

## Consequences

- **Positive:** no work invested in a sunsetting API; one vendor (Duffel) covers flights *and* stays with
  the same SDK; Kiwi fallback plus the aggregator give resilience and coverage; deep-link attribution via
  the per-offer `Source` tag.
- **Negative / trade-off:** primary dependence on Duffel's coverage/commercial terms; hotel/fare APIs are
  partner-gated and ToS-restrict caching, handled by per-plugin rate budgets and graceful degradation;
  in-app booking remains out of scope absent ticketing adoption.

## Alternatives considered

- **Amadeus Self-Service as primary** — decommissioned 2026-07-17; rejected outright.
- **Skyscanner** — approved commercial partners only, no public free tier; not viable as a starter.
- **Travelpayouts-only** — affiliate/commission model, fine for price display + deep-links but weaker as
  the primary search/book source; kept as an alternative, not the lead.
