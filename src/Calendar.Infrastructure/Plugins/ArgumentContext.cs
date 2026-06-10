using System.Globalization;
using Calendar.Plugin.Abstractions;
using Jsonata.Net.Native.Json;

namespace Calendar.Infrastructure.Plugins;

/// <summary>
/// Builds the JSON argument context exposed to the manifest's binding placeholders as <c>q</c>
/// (PLUGIN-HOST.md §5.3). Each capability method projects its arguments into a <see cref="JObject"/> rooted at
/// <c>q</c> so the manifest can reference <c>q.query</c>, <c>q.bias.lang</c>, <c>q.from.lat</c>, etc.
/// </summary>
internal static class ArgumentContext
{
    /// <summary>Context for <see cref="IGeocoder.GeocodeAsync"/> — <c>q.query</c> + optional <c>q.bias.*</c>.</summary>
    public static JToken ForGeocode(string query, GeoBias? bias)
    {
        var q = new JObject();
        q.Add("query", new JValue(query));
        q.Add("bias", BiasObject(bias));
        return Wrap(q);
    }

    /// <summary>Context for <see cref="IGeocoder.ReverseGeocodeAsync"/> — <c>q.lat</c>/<c>q.lng</c>.</summary>
    public static JToken ForReverseGeocode(GeoPoint point)
    {
        var q = new JObject();
        q.Add("lat", new JValue(point.Lat));
        q.Add("lng", new JValue(point.Lng));
        return Wrap(q);
    }

    /// <summary>
    /// Context for <see cref="IRouteProvider.RouteAsync"/> — <c>q.from.{lat,lng}</c>, <c>q.to.{lat,lng}</c>,
    /// <c>q.mode</c> (lowercase mode name), and <c>q.when</c> (ISO-8601 UTC) / <c>q.whenUnix</c> (epoch seconds).
    /// </summary>
    public static JToken ForRoute(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset when)
    {
        var q = new JObject();
        q.Add("from", Point(from));
        q.Add("to", Point(to));
        q.Add("mode", new JValue(mode.ToString().ToLowerInvariant()));
        q.Add("when", new JValue(when.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        q.Add("whenUnix", new JValue(when.ToUnixTimeSeconds()));
        return Wrap(q);
    }

    private static JObject Point(GeoPoint p)
    {
        var o = new JObject();
        o.Add("lat", new JValue(p.Lat));
        o.Add("lng", new JValue(p.Lng));
        return o;
    }

    private static JToken BiasObject(GeoBias? bias)
    {
        if (bias is null)
            return JValue.CreateUndefined();

        var o = new JObject();
        if (bias.Lat is { } lat)
            o.Add("lat", new JValue(lat));
        if (bias.Lng is { } lng)
            o.Add("lng", new JValue(lng));
        if (bias.ViewBox is { } box)
            // Nominatim viewbox order: minLng,minLat,maxLng,maxLat.
            o.Add("viewbox", new JValue(string.Create(CultureInfo.InvariantCulture,
                $"{box.MinLng},{box.MinLat},{box.MaxLng},{box.MaxLat}")));
        if (!string.IsNullOrWhiteSpace(bias.Lang))
            o.Add("lang", new JValue(bias.Lang));
        return o;
    }

    private static JToken Wrap(JObject q)
    {
        var root = new JObject();
        root.Add("q", q);
        return root;
    }
}
