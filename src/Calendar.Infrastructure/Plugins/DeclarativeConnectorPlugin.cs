using Calendar.Plugin.Abstractions;
using Jsonata.Net.Native.Json;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// The host-synthesized <see cref="IPlugin"/> backing a declarative connector (PLUGIN-HOST.md §5,
/// SDK-CONTRACT.md §8). It implements the capability interfaces the manifest declares — today
/// <see cref="IGeocoder"/> (geo.geocode) and <see cref="IRouteProvider"/> (geo.route) — by routing each
/// capability method to its compiled <see cref="OperationPlan"/>. The core cannot tell it apart from a
/// hand-written DLL: it answers the same <c>capability → [plugins]</c> lookups.
/// </summary>
public sealed class DeclarativeConnectorPlugin : IGeocoder, IRouteProvider
{
    private readonly IReadOnlyDictionary<string, OperationPlan> _plans;
    private IPluginHost? _host;

    public DeclarativeConnectorPlugin(
        PluginManifest manifest,
        IReadOnlyDictionary<string, OperationPlan> plans)
    {
        Manifest = manifest;
        _plans = plans;
    }

    public PluginManifest Manifest { get; }

    /// <summary>
    /// A declarative connector advertises the union of routing modes; the aggregator's policy and the
    /// upstream provider decide what is actually answerable. A mode the upstream cannot serve surfaces as a
    /// mapping/HTTP error and fails over (PLUGIN-HOST.md §9).
    /// </summary>
    public RouteCoverage Coverage { get; } = new(Drive: true, Transit: false, Walk: true, Bike: true, TrafficAware: false);

    public Task InitializeAsync(IPluginHost host, CancellationToken ct)
    {
        _host = host;
        return Task.CompletedTask;
    }

    // ---- geo.geocode (IGeocoder) -------------------------------------------------------------------------

    /// <summary>Forward geocode via the <c>geocode</c> operation. Empty/undefined map result → null (no hit).</summary>
    public async Task<Place?> GeocodeAsync(string query, GeoBias? bias, CancellationToken ct)
    {
        var context = ArgumentContext.ForGeocode(query, bias);
        var token = await DispatchAsync("geocode", context, ct).ConfigureAwait(false);
        return DtoMapper.ToPlace(token, Manifest.Id);
    }

    /// <summary>Reverse geocode via the <c>reverseGeocode</c> operation, if the connector declares one.</summary>
    public async Task<Place?> ReverseGeocodeAsync(GeoPoint point, CancellationToken ct)
    {
        if (!_plans.ContainsKey("reverseGeocode") && !_plans.ContainsKey("reverse"))
            throw new NotSupportedException(
                $"connector '{Manifest.Id}' does not declare a reverse-geocode operation.");

        var opName = _plans.ContainsKey("reverseGeocode") ? "reverseGeocode" : "reverse";
        var context = ArgumentContext.ForReverseGeocode(point);
        var token = await DispatchAsync(opName, context, ct).ConfigureAwait(false);
        return DtoMapper.ToPlace(token, Manifest.Id);
    }

    // ---- geo.route (IRouteProvider) ----------------------------------------------------------------------

    /// <summary>
    /// Route via the <c>route</c> operation. The connector supplies only <c>DurationSec</c> (+ optional
    /// <c>Geometry</c>); the host forces <c>LeaveByUtc = null</c> and <c>Feasible = true</c> because those are
    /// host-computed against the inter-event gap (SDK-CONTRACT.md §6.3, PLUGIN-HOST.md §5.6).
    /// </summary>
    public async Task<RouteResult> RouteAsync(
        GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when, CancellationToken ct)
    {
        var context = ArgumentContext.ForRoute(from, to, mode, when);
        var token = await DispatchAsync("route", context, ct).ConfigureAwait(false);
        return DtoMapper.ToRouteResult(token, Manifest.Id);
    }

    // ------------------------------------------------------------------------------------------------------

    private async Task<JToken> DispatchAsync(string operationName, JToken context, CancellationToken ct)
    {
        if (_host is null)
            throw new InvalidOperationException(
                $"connector '{Manifest.Id}' was not initialized before a capability call.");
        if (!_plans.TryGetValue(operationName, out var plan))
            throw new NotSupportedException(
                $"connector '{Manifest.Id}' does not declare operation '{operationName}'.");

        using var request = plan.BuildRequest(context);

        // The broker applies the manifest auth scheme; the connector never sees the secret (PLUGIN-HOST.md §6).
        await _host.Auth.ApplyAsync(request, Manifest.Auth.Scopes, ct).ConfigureAwait(false);

        var client = _host.CreateClient();
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 404 on a single-result op is a legitimate empty (PLUGIN-HOST.md §5.8) → map to undefined/null.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return JValue.CreateUndefined();

        if (!response.IsSuccessStatusCode)
            throw new ConnectorMappingException(
                $"connector '{Manifest.Id}' operation '{operationName}': upstream returned {(int)response.StatusCode}.");

        return plan.MapResponse(body);
    }
}
