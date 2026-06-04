# 0012 — Self-hosted geo by default; commercial as optional keyed plugins

**Status:** Accepted (2026-06)

## Context

Geocoding, places, and routing send location text to an external service, so defaults must respect
privacy and cost. Critically, the **public Nominatim/OSM endpoints cannot be used in a shipped app**:
the policy caps at 1 req/s, mandates caching/attribution, and **explicitly forbids no-code/LLM-generated
generic geocoding**. The public OSM tile server and Photon demo server are likewise not for production
load ([ARCHITECTURE.md §7 Map, geocoding & routing](../ARCHITECTURE.md#7-map-geocoding--routing),
[PLUGIN-RESEARCH.md §Phase 2 geo](../PLUGIN-RESEARCH.md#phase-2--geo), Nominatim usage policy).

## Decision

Default the geo capabilities to **self-hosted, open providers**: **Nominatim** (`geo.geocode`),
**Photon** (`geo.places`), **OSRM/Valhalla** (`geo.route`). Self-hosting removes quotas and keeps
location data local; every result is cached (`GeocodeCache`, per-leg `RouteLeg`). **Commercial providers
(Google, Mapbox, LocationIQ) are optional, user-keyed plugins** behind the fallback aggregator —
e.g. Google for transit/traffic-aware routing the self-hosted engines can't provide. **The public
Nominatim/OSM endpoints must never appear in shipped manifests.**

## Consequences

- **Positive:** private and quota-free by default; compliant with the Nominatim policy; commercial
  upgrades are opt-in and swap in trivially via the pluggable capability + aggregator
  ([0009](0009-multi-provider-fallback-aggregator.md)); aggressive caching minimizes outbound calls and
  cost.
- **Negative / trade-off:** self-hosting has real operational weight (Photon planet index ≈ 95 GB; OSRM/
  Valhalla data + tuning); OSRM/Valhalla offer **no public-transit schedules and no live traffic**, so
  the "leave by / you can't make it" feature needs a commercial provider for transit/traffic
  ([0013](0013-maplibre-and-routes-api.md)); commercial plugins require a billing account + key and
  careful (session-token) cost control.

## Alternatives considered

- **Public Nominatim/OSM endpoints in the shipped app** — violates the usage policy (rate limits,
  no-code/LLM prohibition); rejected outright.
- **Commercial geo as the default** — best quality but paid, key-gated, and privacy-leaking by default;
  made opt-in instead.
