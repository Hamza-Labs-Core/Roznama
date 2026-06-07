// MapLibre GL JS interop module (geo-tiles-plugin.md §4). The C# MapView owns data + intent (which style,
// which events, which date window); this module owns the WebGL map instance. The bridge is intentionally
// small: initMap, setStyle, setEvents, dispose, plus a dotNet callback for pin selection.
//
// All map STATE lives here; all map INTENT lives in C#. MapLibre GL JS is loaded globally from the CDN in
// index.html (window.maplibregl); if it is absent (offline first load with no cache) we degrade to an empty
// state rather than throwing.

// One live map per element id. Keyed so repeated Map-view opens don't leak instances across view switches.
const maps = new Map();

const EVENTS_SOURCE = "uc-events";
const CLUSTER_LAYER = "uc-clusters";
const CLUSTER_COUNT_LAYER = "uc-cluster-count";
const PIN_LAYER = "uc-unclustered-pin";

function resolveStyle(descriptor) {
    // StyleUrl and StyleJson are mutually exclusive (StyleDescriptor). MapLibre accepts a URL string or a
    // parsed style object.
    if (descriptor && descriptor.styleUrl) return descriptor.styleUrl;
    if (descriptor && descriptor.styleJson) {
        try { return JSON.parse(descriptor.styleJson); }
        catch { /* fall through to empty style */ }
    }
    // Last-resort empty style so the canvas still mounts (the host always sends a valid one in practice).
    return { version: 8, sources: {}, layers: [] };
}

export function initMap(elementId, descriptor, dotNetRef) {
    if (typeof window === "undefined" || !window.maplibregl) {
        // MapLibre GL JS didn't load (offline, blocked CDN). Surface the empty state; never throw into Blazor.
        const el = document.getElementById(elementId);
        if (el) el.setAttribute("data-maplibre-missing", "true");
        return false;
    }

    // Dispose any prior instance for this element (defensive against double-init on re-render).
    disposeMap(elementId);

    const map = new window.maplibregl.Map({
        container: elementId,
        style: resolveStyle(descriptor),
        center: [0, 20],
        zoom: 1.2,
        attributionControl: false,
    });

    map.addControl(
        new window.maplibregl.AttributionControl({
            compact: true,
            customAttribution: descriptor ? descriptor.attribution : undefined,
        }),
    );
    map.addControl(new window.maplibregl.NavigationControl({ showCompass: false }), "top-right");

    const state = { map, dotNetRef, pending: null, ready: false };
    maps.set(elementId, state);

    map.on("load", () => {
        addEventLayers(map);
        state.ready = true;
        if (state.pending) {
            applyEvents(elementId, state.pending);
            state.pending = null;
        }
    });

    return true;
}

function addEventLayers(map) {
    map.addSource(EVENTS_SOURCE, {
        type: "geojson",
        data: emptyCollection(),
        cluster: true,
        clusterMaxZoom: 14,
        clusterRadius: 50,
    });

    // Cluster bubbles — sized by point_count (standard MapLibre clustering pattern, §4).
    map.addLayer({
        id: CLUSTER_LAYER,
        type: "circle",
        source: EVENTS_SOURCE,
        filter: ["has", "point_count"],
        paint: {
            "circle-color": "#3b6ef5",
            "circle-opacity": 0.85,
            "circle-radius": ["step", ["get", "point_count"], 16, 10, 22, 50, 30],
        },
    });

    map.addLayer({
        id: CLUSTER_COUNT_LAYER,
        type: "symbol",
        source: EVENTS_SOURCE,
        filter: ["has", "point_count"],
        layout: {
            "text-field": ["get", "point_count_abbreviated"],
            "text-size": 12,
        },
        paint: { "text-color": "#ffffff" },
    });

    // Unclustered pins — colored by source calendar (the per-feature "color" property), §4.
    map.addLayer({
        id: PIN_LAYER,
        type: "circle",
        source: EVENTS_SOURCE,
        filter: ["!", ["has", "point_count"]],
        paint: {
            "circle-color": ["coalesce", ["get", "color"], "#e8590c"],
            "circle-radius": 7,
            "circle-stroke-width": 2,
            "circle-stroke-color": "#ffffff",
        },
    });

    // Click a cluster → zoom in to expand it.
    map.on("click", CLUSTER_LAYER, (e) => {
        const features = map.queryRenderedFeatures(e.point, { layers: [CLUSTER_LAYER] });
        const clusterId = features[0]?.properties?.cluster_id;
        const src = map.getSource(EVENTS_SOURCE);
        if (clusterId == null || !src) return;
        src.getClusterExpansionZoom(clusterId, (err, zoom) => {
            if (err) return;
            map.easeTo({ center: features[0].geometry.coordinates, zoom });
        });
    });

    // Click a pin → popup + raise OnPinSelected into C# so the Inspector can show the event.
    map.on("click", PIN_LAYER, (e) => {
        const f = e.features && e.features[0];
        if (!f) return;
        const p = f.properties || {};
        const coords = f.geometry.coordinates.slice();
        new window.maplibregl.Popup({ closeButton: true })
            .setLngLat(coords)
            .setHTML(
                `<strong>${escapeHtml(p.title || "Event")}</strong>` +
                (p.placeLabel ? `<br/><span>${escapeHtml(p.placeLabel)}</span>` : ""),
            )
            .addTo(map);
        const state = stateForMap(map);
        if (state && state.dotNetRef && p.id) {
            state.dotNetRef.invokeMethodAsync("OnPinSelected", p.id);
        }
    });

    map.on("mouseenter", PIN_LAYER, () => { map.getCanvas().style.cursor = "pointer"; });
    map.on("mouseleave", PIN_LAYER, () => { map.getCanvas().style.cursor = ""; });
    map.on("mouseenter", CLUSTER_LAYER, () => { map.getCanvas().style.cursor = "pointer"; });
    map.on("mouseleave", CLUSTER_LAYER, () => { map.getCanvas().style.cursor = ""; });
}

// Replace all pins in one setData (no per-pin DOM) — the §4/§7 performance pattern.
export function setEvents(elementId, pins) {
    const state = maps.get(elementId);
    if (!state) return;
    if (!state.ready) { state.pending = pins; return; }
    applyEvents(elementId, pins);
}

function applyEvents(elementId, pins) {
    const state = maps.get(elementId);
    if (!state) return;
    const { map } = state;
    const src = map.getSource(EVENTS_SOURCE);
    if (!src) return;

    const collection = toCollection(pins);
    src.setData(collection);

    // Fit bounds to the pins; empty state is handled in C# (the map just stays at the world view).
    if (collection.features.length > 0) {
        const bounds = new window.maplibregl.LngLatBounds();
        for (const feat of collection.features) {
            bounds.extend(feat.geometry.coordinates);
        }
        map.fitBounds(bounds, { padding: 48, maxZoom: 11, duration: 400 });
    }
}

export function setStyle(elementId, descriptor) {
    const state = maps.get(elementId);
    if (!state) return;
    const { map } = state;
    // setStyle drops custom sources/layers — re-add them and re-apply pins on the new style's load (§10).
    const reapply = state.pending;
    state.ready = false;
    map.setStyle(resolveStyle(descriptor));
    map.once("load", () => {
        addEventLayers(map);
        state.ready = true;
        if (reapply) { applyEvents(elementId, reapply); }
    });
}

export function disposeMap(elementId) {
    const state = maps.get(elementId);
    if (!state) return;
    try { state.map.remove(); } catch { /* already gone */ }
    maps.delete(elementId);
}

function stateForMap(map) {
    for (const s of maps.values()) {
        if (s.map === map) return s;
    }
    return null;
}

function toCollection(pins) {
    if (!Array.isArray(pins)) return emptyCollection();
    return {
        type: "FeatureCollection",
        features: pins
            .filter((p) => p && isFinite(p.lng) && isFinite(p.lat))
            .map((p) => ({
                type: "Feature",
                geometry: { type: "Point", coordinates: [p.lng, p.lat] },
                properties: {
                    id: p.id,
                    title: p.title,
                    color: p.color || null,
                    placeLabel: p.placeLabel || null,
                },
            })),
    };
}

function emptyCollection() {
    return { type: "FeatureCollection", features: [] };
}

function escapeHtml(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;");
}
