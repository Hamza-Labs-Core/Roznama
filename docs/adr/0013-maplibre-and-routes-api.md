# 0013 — Maps via MapLibre GL; routing via Google Routes API v2

**Status:** Accepted (2026-06)

## Context

The map view needs a renderer that is open, self-hostable, and not license-locked to one tile vendor,
since tiles are a pluggable `geo.tiles` capability. Separately, when a commercial routing provider *is*
used for transit/traffic-aware timing, it must be the **current, supported** Google API — the legacy
Google Directions API is superseded by the Routes API v2
([ARCHITECTURE.md §2](../ARCHITECTURE.md#2-technology-stack),
[§7](../ARCHITECTURE.md#7-map-geocoding--routing),
[PLUGIN-RESEARCH.md §Phase 2 geo](../PLUGIN-RESEARCH.md#phase-2--geo),
[UI.md §5 Map view](../UI.md#5-map-view)).

## Decision

Render the map with **MapLibre GL JS** (open, BSD-3-licensed, vector tiles, self-hostable) via Blazor JS
interop. Tiles come from a `geo.tiles` plugin — **MapLibre/OSM by default**, with Google or Mapbox as
alternative keyed plugins; `geo.tiles` is a single chosen basemap, not aggregated (see
[0009](0009-multi-provider-fallback-aggregator.md)). For commercial `geo.route`, target the **Google
Routes API v2** (transit + traffic-aware), **not** the legacy Directions API, as the optional paid
upgrade over the self-hosted OSRM/Valhalla default ([0012](0012-self-hosted-geo-default.md)).

## Consequences

- **Positive:** MapLibre's permissive BSD-3 license avoids Mapbox GL's restrictive terms while keeping a
  Mapbox/Google tile plugin available; vector tiles + clustering scale to thousands of pins
  ([UI.md §9](../UI.md#9-performance)); building on Routes API v2 avoids adopting a legacy API and gets
  the transit/traffic timing the planner's "leave by" feature needs.
- **Negative / trade-off:** MapLibre requires JS interop and a JS dependency in a .NET app; the Google
  Routes plugin is paid, needs a Cloud billing account + key, and is usage-priced — so it stays an
  optional, user-configured plugin behind the aggregator, with self-hosted routing as the free default.

## Alternatives considered

- **Mapbox GL JS** as the renderer — strong, but its license is restrictive and would couple rendering
  to Mapbox; MapLibre (the open fork) chosen, with Mapbox kept as a tile *plugin*.
- **Google Directions API (legacy)** for routing — superseded by Routes API v2; rejected to avoid
  building on a deprecated API.
