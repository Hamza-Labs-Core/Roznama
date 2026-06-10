// EventSource interop for GET /api/sync/stream (API.md, UI.md §9). The browser owns the SSE connection
// (auto-reconnect included); C# owns what to reload per event. Bridge: connect → forward named events into
// DotNet, disconnect → close. Kept tiny on purpose, mirroring map.js.

const sources = new Map();
let nextId = 1;

export function connect(dotNetRef) {
    if (typeof window === "undefined" || !window.EventSource) {
        return 0; // ancient browser / prerender — live refresh degrades to manual reload.
    }

    const id = nextId++;
    const es = new EventSource("api/sync/stream");
    const forward = (type) => (e) => {
        dotNetRef.invokeMethodAsync("OnServerEvent", type, e.data || "{}").catch(() => { });
    };
    es.addEventListener("eventsChanged", forward("eventsChanged"));
    es.addEventListener("syncProgress", forward("syncProgress"));
    es.addEventListener("notificationsChanged", forward("notificationsChanged"));
    sources.set(id, es);
    return id;
}

export function disconnect(id) {
    const es = sources.get(id);
    if (es) {
        es.close();
        sources.delete(id);
    }
}
