using System.Globalization;
using System.Text.Json;
using Calendar.Plugin.Abstractions;

namespace Calendar.Plugin.Duffel;

/// <summary>
/// Pure JSON → domain mapping for Duffel payloads (travel-fares-plugin.md §8). Stateless and side-effect-free
/// so the offer/result shapes are unit-testable without HTTP. Every offer is stamped <c>Source="duffel"</c>,
/// <c>RetrievedAt=now</c>, <c>Stale=false</c> (the aggregator flips <c>Stale</c> only when it serves a
/// last-known fallback from history — §9).
/// </summary>
public static class DuffelNormalizer
{
    /// <summary>
    /// One Duffel offer → <see cref="FareOffer"/>. From/To come from the first slice origin / last slice
    /// destination; carrier from <c>owner.iata_code</c>; flight no + segment times from the first/last segment
    /// of the first slice. Returns null when the offer lacks a usable price (skipped, not faulted).
    /// </summary>
    public static FareOffer? NormalizeFareOffer(JsonElement offer, FareQuery q, DateTimeOffset retrievedAt)
    {
        if (!TryGetDecimal(offer, "total_amount", out var price))
            return null;

        var currency = GetString(offer, "total_currency") ?? q.Currency;

        var slices = GetArray(offer, "slices");
        var firstSlice = slices.Count > 0 ? slices[0] : (JsonElement?)null;
        var lastSlice = slices.Count > 0 ? slices[^1] : (JsonElement?)null;

        var from = firstSlice is { } fs ? GetIata(fs, "origin") ?? q.From : q.From;
        var to = lastSlice is { } ls ? GetIata(ls, "destination") ?? q.To : q.To;

        var segments = firstSlice is { } sl ? GetArray(sl, "segments") : new List<JsonElement>();
        var firstSeg = segments.Count > 0 ? segments[0] : (JsonElement?)null;
        var lastSeg = segments.Count > 0 ? segments[^1] : (JsonElement?)null;

        var carrier = GetString(offer.TryGetProperty("owner", out var owner) ? owner : default, "iata_code") ?? string.Empty;
        var flightNo = firstSeg is { } seg ? GetString(seg, "operating_carrier_flight_number") ?? string.Empty : string.Empty;

        var departUtc = firstSeg is { } d && TryGetDateTimeOffset(d, "departing_at", out var dep)
            ? dep : new DateTimeOffset(q.DepartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var arriveUtc = lastSeg is { } a && TryGetDateTimeOffset(a, "arriving_at", out var arr)
            ? arr : departUtc;

        var date = DateOnly.FromDateTime(departUtc.UtcDateTime);
        var offerId = GetString(offer, "id");
        var deepLink = offerId is null ? "https://www.duffel.com/" : $"https://www.duffel.com/{offerId}";

        return new FareOffer(
            Price: price,
            Currency: currency,
            From: from,
            To: to,
            Date: date,
            Carrier: carrier,
            FlightNo: flightNo,
            DepartUtc: departUtc,
            ArriveUtc: arriveUtc,
            DeepLink: deepLink,
            Source: DuffelPlugin.SourceTag,
            RetrievedAt: retrievedAt,
            Stale: false);
    }

    /// <summary>
    /// One Duffel stays search result → <see cref="StayOffer"/>. <c>PriceTotal</c> = <c>cheapest_rate_total_amount</c>;
    /// <c>PricePerNight</c> = total ÷ nights; label + coordinates from <c>accommodation.location</c>;
    /// check-in/out echo the query (travel-fares-plugin.md §8). Returns null without a usable rate.
    /// </summary>
    public static StayOffer? NormalizeStayOffer(JsonElement result, StayQuery q, int nights, DateTimeOffset retrievedAt)
    {
        if (!TryGetDecimal(result, "cheapest_rate_total_amount", out var total))
            return null;

        var currency = GetString(result, "cheapest_rate_currency") ?? q.Currency;
        var safeNights = Math.Max(1, nights);
        var perNight = Math.Round(total / safeNights, 2, MidpointRounding.AwayFromZero);

        var accommodation = result.TryGetProperty("accommodation", out var acc) ? acc : default;
        var placeLabel = GetString(accommodation, "name") ?? "Accommodation";

        var lat = q.Lat;
        var lng = q.Lng;
        if (accommodation.ValueKind == JsonValueKind.Object &&
            accommodation.TryGetProperty("location", out var location) &&
            location.TryGetProperty("geographic_coordinates", out var coords))
        {
            if (TryGetDouble(coords, "latitude", out var plat)) lat = plat;
            if (TryGetDouble(coords, "longitude", out var plng)) lng = plng;
        }

        var resultId = GetString(result, "id");
        var deepLink = resultId is null ? "https://www.duffel.com/stays" : $"https://www.duffel.com/stays/{resultId}";

        return new StayOffer(
            PricePerNight: perNight,
            PriceTotal: total,
            Currency: currency,
            PlaceLabel: placeLabel,
            Lat: lat,
            Lng: lng,
            CheckIn: q.CheckIn,
            CheckOut: q.CheckOut,
            DeepLink: deepLink,
            Source: DuffelPlugin.SourceTag,
            RetrievedAt: retrievedAt,
            Stale: false);
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────────────────────────────

    private static List<JsonElement> GetArray(JsonElement parent, string property)
    {
        var list = new List<JsonElement>();
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(property, out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                list.Add(item);
        }
        return list;
    }

    private static string? GetIata(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var node)
            ? GetString(node, "iata_code")
            : null;

    private static string? GetString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Duffel amounts are JSON strings (e.g. <c>"123.45"</c>); accept numbers too for resilience.</summary>
    private static bool TryGetDecimal(JsonElement parent, string property, out decimal value)
    {
        value = 0m;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var node))
            return false;

        return node.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(node.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => node.TryGetDecimal(out value),
            _ => false,
        };
    }

    private static bool TryGetDouble(JsonElement parent, string property, out double value)
    {
        value = 0d;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out var node))
            return false;

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(node.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool TryGetDateTimeOffset(JsonElement parent, string property, out DateTimeOffset value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var node) ||
            node.ValueKind != JsonValueKind.String)
            return false;

        var raw = node.GetString();
        if (string.IsNullOrEmpty(raw))
            return false;

        // Duffel segment times are local ISO-8601 without an offset (e.g. "2026-08-12T11:30:00"); treat as UTC
        // for a stable dedupe key (travel-fares-plugin.md §9 keys on departUtc/arriveUtc to the minute).
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            return true;

        return false;
    }
}
