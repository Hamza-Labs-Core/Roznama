using Calendar.Domain;

namespace Calendar.Application.Fares;

/// <summary>
/// Create/list/delete fare watches, poll them against the interchangeable <c>*.price</c> aggregators, and read
/// a watch's price history (ARCHITECTURE.md §14, docs/deep-dives/travel-fares-plugin.md §10).
///
/// <para>Each <see cref="PollAsync"/> builds a <c>FareQuery</c>/<c>StayQuery</c> from every active watch, runs it
/// through the fan-out aggregator (dedupe + cheapest + source-tag + stale last-known), appends a timestamped
/// <c>FareSample</c>, and — when the new low drops below the prior low by the watch threshold OR crosses the
/// user's target — fires <b>exactly one</b> notification (recorded in the persisted in-app log). With no provider
/// registered/configured a poll simply records nothing and never throws (travel-fares-plugin.md §13).</para>
/// </summary>
public interface IFareWatchService
{
    /// <summary>Create a watch and return its server-assigned view.</summary>
    Task<FareWatchDto> CreateAsync(CreateFareWatchRequest request, CancellationToken ct);

    /// <summary>List active (non-deleted) watches, newest-first.</summary>
    Task<IReadOnlyList<FareWatchDto>> ListAsync(CancellationToken ct);

    /// <summary>Soft-delete a watch (tombstone) so polling stops. Returns false when it doesn't exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>The watch's price series, newest sample first (the detail chart's data).</summary>
    Task<IReadOnlyList<FareSampleDto>> GetHistoryAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Poll every active watch once (the manual trigger and the hosted background loop both call this). Returns
    /// the per-watch outcome (sample appended, whether a notification fired).
    /// </summary>
    Task<PollSummary> PollAsync(CancellationToken ct);
}

/// <summary>A request to start tracking a route (flight) or stay (DATA-SCHEMA §2.6).</summary>
public sealed record CreateFareWatchRequest(
    FareKind Kind,
    DateOnly RangeStart, DateOnly RangeEnd,
    int Pax = 1,
    string? Currency = null,
    decimal? TargetPrice = null,
    double? DropThreshold = null,
    // Flight
    string? OriginIata = null, string? DestIata = null,
    // Stay
    double? Lat = null, double? Lng = null, int? RadiusKm = null, int? Rooms = null,
    Guid? OriginPlaceId = null, Guid? DestPlaceId = null);

/// <summary>A watch as returned to the API.</summary>
public sealed record FareWatchDto(
    Guid Id, FareKind Kind,
    DateOnly RangeStart, DateOnly RangeEnd,
    int Pax, string Currency,
    decimal? TargetPrice, decimal? LastLowPrice, double DropThreshold,
    bool IsActive,
    string? OriginIata, string? DestIata,
    double? Lat, double? Lng, int? RadiusKm, int? Rooms);

/// <summary>One price-history point.</summary>
public sealed record FareSampleDto(
    Guid Id, DateTimeOffset SampledAtUtc,
    decimal Price, string Currency, string Source, bool IsStale);

/// <summary>The result of one <see cref="IFareWatchService.PollAsync"/> sweep.</summary>
public sealed record PollSummary(int WatchesPolled, int SamplesRecorded, int NotificationsFired)
{
    public static PollSummary Empty { get; } = new(0, 0, 0);
}
