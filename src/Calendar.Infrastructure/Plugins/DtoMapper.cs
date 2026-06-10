using System.Globalization;
using Calendar.Plugin.Abstractions;
using Jsonata.Net.Native.Json;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Projects a JSONata <c>map</c> result (a <see cref="JToken"/>) into the exact SDK return DTO
/// (PLUGIN-HOST.md §5.6). Source is stamped to the manifest id when the map didn't set it, so the aggregator
/// always has a winning-provider tag. Host-computed routing fields are forced regardless of what the map emits.
/// </summary>
internal static class DtoMapper
{
    /// <summary>
    /// <c>geo.geocode</c> → <see cref="Place"/>?. An empty/undefined/null map result is the normal "no result"
    /// case → null. A non-object result that lacks coordinates is a mapping fault.
    /// </summary>
    public static Place? ToPlace(JToken token, string pluginId)
    {
        if (token.Type is JTokenType.Undefined or JTokenType.Null)
            return null;

        // A map that returned an array (forgot the $[0]) → take the first element, or null if empty.
        if (token is JArray array)
        {
            if (array.ChildrenTokens.Count == 0)
                return null;
            token = array.ChildrenTokens[0];
        }

        if (token is not JObject obj)
            throw new ConnectorMappingException(
                $"connector '{pluginId}': geocode map did not produce an object.");

        var lat = TryNumber(obj, "Lat") ?? TryNumber(obj, "lat");
        var lng = TryNumber(obj, "Lng") ?? TryNumber(obj, "lng");
        if (lat is null || lng is null)
            throw new ConnectorMappingException(
                $"connector '{pluginId}': geocode map result is missing Lat/Lng.");

        var label = TryString(obj, "Label") ?? TryString(obj, "label") ?? string.Empty;
        var address = TryString(obj, "Address") ?? TryString(obj, "address");
        var source = TryString(obj, "Source") ?? TryString(obj, "source");
        if (string.IsNullOrWhiteSpace(source))
            source = pluginId;

        return new Place(lat.Value, lng.Value, label, address, source);
    }

    /// <summary>
    /// <c>geo.route</c> → <see cref="RouteResult"/>. The connector supplies <c>DurationSec</c> (+ optional
    /// <c>Geometry</c>) only; <c>LeaveByUtc</c> is forced null and <c>Feasible</c> forced true — those are
    /// host-computed against the inter-event gap (SDK-CONTRACT.md §6.3). A map that tries to set them is ignored.
    /// </summary>
    public static RouteResult ToRouteResult(JToken token, string pluginId)
    {
        if (token is JArray arr && arr.ChildrenTokens.Count > 0)
            token = arr.ChildrenTokens[0];

        if (token is not JObject obj)
            throw new ConnectorMappingException(
                $"connector '{pluginId}': route map did not produce an object.");

        var duration = TryNumber(obj, "DurationSec") ?? TryNumber(obj, "durationSec") ?? TryNumber(obj, "duration");
        if (duration is null)
            throw new ConnectorMappingException(
                $"connector '{pluginId}': route map result is missing DurationSec.");

        var geometry = TryString(obj, "Geometry") ?? TryString(obj, "geometry");
        var source = TryString(obj, "Source") ?? TryString(obj, "source");
        if (string.IsNullOrWhiteSpace(source))
            source = pluginId;

        // LeaveByUtc and Feasible are host-computed — forced here so a connector cannot invent them.
        return new RouteResult(
            DurationSec: checked((int)Math.Round(duration.Value)),
            Geometry: geometry,
            LeaveByUtc: null,
            Feasible: true,
            Source: source);
    }

    private static double? TryNumber(JObject obj, string key)
    {
        if (!TryGet(obj, key, out var value))
            return null;
        return value.Type switch
        {
            JTokenType.Integer => value.ToObject<long>(),
            JTokenType.Float => value.ToObject<double>(),
            JTokenType.String when double.TryParse(value.ToObject<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static string? TryString(JObject obj, string key)
    {
        if (!TryGet(obj, key, out var value))
            return null;
        return value.Type switch
        {
            JTokenType.String => value.ToObject<string>(),
            JTokenType.Null or JTokenType.Undefined => null,
            _ => value.ToFlatString()
        };
    }

    private static bool TryGet(JObject obj, string key, out JToken value)
    {
        if (obj.Properties.TryGetValue(key, out var found) && found.Type is not JTokenType.Undefined)
        {
            value = found;
            return true;
        }

        value = JValue.CreateUndefined();
        return false;
    }
}
