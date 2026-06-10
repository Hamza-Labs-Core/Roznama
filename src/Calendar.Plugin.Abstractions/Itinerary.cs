namespace Calendar.Plugin.Abstractions;

/// <summary>capability <c>itinerary.import</c>. Import booked trips as Trip + TripItem; items project into Events.</summary>
public interface IItinerarySource : IPlugin
{
    /// <summary>Import trips overlapping the window. Delta semantics mirror calendars: null token = full import.</summary>
    Task<ItineraryResult> ImportAsync(DateOnly from, DateOnly to, string? syncToken, CancellationToken ct);
}

/// <summary>Kinds of trip item (ARCHITECTURE.md §9 TRIP_ITEM.Kind).</summary>
public enum TripItemKind { Flight, Stay, Car, Rail, Activity }

/// <summary>Whether a trip item is confirmed or a planner draft (ARCHITECTURE.md §9 TRIP_ITEM.Status).</summary>
public enum TripItemStatus { Booked, Candidate }

/// <summary>A trip groups items over a date span. Maps to the domain TRIP.</summary>
public sealed record Trip(
    string RemoteId,
    string Name,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<TripItem> Items);

/// <summary>One item within a trip. Booked items project into Events (Travel category); candidates feed planner overlays.</summary>
public sealed record TripItem(
    string RemoteId,
    TripItemKind Kind,
    TripItemStatus Status,
    string Title,                               // e.g. "BA117 JFK→LHR", "Hilton Berlin"
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? Location,                           // free-form → geocoded to a Place
    GeoPoint? Geo,                              // explicit coords when known
    string? Confirmation,                       // confirmation/booking reference
    string? DeepLink);                          // link out to the booking/itinerary

/// <summary>Result of one itinerary import round (same delta shape as <see cref="SyncResult"/>).</summary>
public sealed record ItineraryResult(
    IReadOnlyList<Trip> Upserts,
    IReadOnlyList<string> Deletes,              // RemoteIds of removed trips
    string NewSyncToken);

/// <summary>capability <c>flight.price</c>. Search interchangeable fare offers (aggregated, deduped, cheapest-picked).</summary>
public interface IFlightPricing : IPlugin
{
    /// <summary>Markets/currencies/route-shapes this provider serves — the aggregator's routing policy reads it.</summary>
    FareCoverage Coverage { get; }

    Task<IReadOnlyList<FareOffer>> SearchAsync(FareQuery q, CancellationToken ct);
}

/// <summary>capability <c>stay.price</c>. Search interchangeable nightly/total stay offers.</summary>
public interface IStayPricing : IPlugin
{
    FareCoverage Coverage { get; }

    Task<IReadOnlyList<StayOffer>> SearchAsync(StayQuery q, CancellationToken ct);
}

public enum CabinClass { Economy, PremiumEconomy, Business, First }

public sealed record FareQuery(
    string From, string To,                     // IATA codes (city/place → IATA resolved upstream)
    DateOnly DepartDate, DateOnly? ReturnDate,
    int Adults, CabinClass Cabin, string Currency, string Market);

public sealed record StayQuery(
    double Lat, double Lng, int RadiusKm,       // geographic search (or an accommodation id upstream)
    DateOnly CheckIn, DateOnly CheckOut,
    int Adults, int Rooms, string Currency, string Market);

public sealed record FareOffer(
    decimal Price, string Currency,
    string From, string To, DateOnly Date,
    string Carrier, string FlightNo,            // dedupe key (with times); first marketing carrier for multi-segment
    DateTimeOffset DepartUtc, DateTimeOffset ArriveUtc,
    string DeepLink, string Source,             // Source = winning provider id: "duffel" | "kiwi" | …
    DateTimeOffset RetrievedAt, bool Stale);    // timestamped; Stale=true when served from history fallback

public sealed record StayOffer(
    decimal PricePerNight, decimal PriceTotal, string Currency,
    string PlaceLabel, double Lat, double Lng,
    DateOnly CheckIn, DateOnly CheckOut,
    string DeepLink, string Source,
    DateTimeOffset RetrievedAt, bool Stale);

/// <summary>A pricing provider's coverage descriptor, read by the aggregator before fanning a query out.</summary>
public sealed record FareCoverage(
    IReadOnlySet<string> Markets,
    IReadOnlySet<string> Currencies,
    bool MultiCity, bool OneWay, string Notes);
