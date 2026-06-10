namespace Calendar.Web.Services;

/// <summary>A projected event instance from <c>GET /api/events</c>.</summary>
public sealed record EventDto(
    Guid Id,
    Guid CalendarId,
    string CalendarName,
    string? Color,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location,
    bool IsRecurringInstance,
    Guid? MasterId,
    int DuplicateCount,
    IReadOnlyList<string> Categories);

public sealed record AccountDto(Guid Id, string PluginId, string DisplayName, string Status, DateTimeOffset? LastSyncAtUtc);

public sealed record CalendarDto(Guid Id, Guid AccountId, string Name, string? Color, bool IsVisible, bool IsReadOnly);

public sealed record CategoryDto(Guid Id, string Name, bool IsVisible, bool IsBuiltIn);

/// <summary>Body for <c>POST /api/accounts</c>: a bare <see cref="FeedUrl"/> is the zero-OAuth ICS path;
/// otherwise <see cref="PluginId"/> + <see cref="Config"/> runs that plugin's declared auth scheme.</summary>
public sealed record ConnectAccountBody(
    string? PluginId, string? FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory,
    Dictionary<string, string>? Config);

/// <summary>The connect outcome: either a finished account or an OAuth redirect challenge.</summary>
public sealed record ConnectAccountResult(Guid? Id, AuthChallengeDto? AuthChallenge);

/// <summary>The provider redirect from an OAuth connect (<c>POST /api/accounts</c>).</summary>
public sealed record AuthChallengeDto(string RedirectUrl, string State);

/// <summary>An installed plugin from <c>GET /api/plugins</c>; <see cref="AuthScheme"/> drives the Add-account UI.</summary>
public sealed record PluginDto(
    string Id, string Name, string Version, string Kind, string State,
    IReadOnlyList<string> Capabilities, string? FaultReason, string AuthScheme);

/// <summary>A map pin from <c>GET /api/map/events</c>: an event with resolved coordinates (ARCHITECTURE §7).</summary>
public sealed record MapEventDto(
    Guid Id,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double Lat,
    double Lng,
    string PlaceLabel,
    string? Color);

/// <summary>One copy inside a duplicate group (<c>GET /api/events/{id}/duplicates</c>).</summary>
public sealed record DuplicateMemberDto(
    Guid EventId,
    string Uid,
    string Title,
    DateTimeOffset StartUtc,
    string CalendarName,
    string AccountName,
    bool IsCanonical);

/// <summary>A duplicate group for the inspector dialog.</summary>
public sealed record DuplicateGroupDto(Guid GroupId, IReadOnlyList<DuplicateMemberDto> Members);

/// <summary>A reminder from <c>GET /api/reminders</c> (Phase 6 polish).</summary>
public sealed record ReminderDto(
    Guid Id,
    Guid EventId,
    string EventTitle,
    DateTimeOffset EventStartUtc,
    int LeadMinutes,
    DateTimeOffset? FiredAtUtc);

/// <summary>Body for <c>POST /api/plugins/install</c> (marketplace).</summary>
public sealed record InstallPluginBody(
    string DownloadUrl,
    string? Sha256 = null,
    string? Signature = null,
    string? PublisherName = null);

/// <summary>The installed plugin returned by <c>POST /api/plugins/install</c>.</summary>
public sealed record InstalledPluginDto(
    string Id,
    string Name,
    string Version,
    string State,
    string TrustTier,
    string? FaultReason);

/// <summary>One search hit from <c>GET /api/search</c> (Phase 6 polish).</summary>
public sealed record SearchResultDto(
    Guid Id,
    Guid CalendarId,
    string CalendarName,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location);

/// <summary>A trip from <c>GET /api/map/trips</c>: ordered legs between Travel-category stops (Phase 5).</summary>
public sealed record TripRouteDto(Guid TripId, string Name, IReadOnlyList<TripLegDto> Legs);

/// <summary>One trip leg; <see cref="Geometry"/> is an encoded polyline or null (⇒ draw a straight line).</summary>
public sealed record TripLegDto(
    Guid FromEventId,
    Guid ToEventId,
    string FromLabel,
    string ToLabel,
    double FromLat,
    double FromLng,
    double ToLat,
    double ToLng,
    DateTimeOffset DepartUtc,
    DateTimeOffset ArriveUtc,
    string? Geometry,
    string? Source);

/// <summary>
/// The resolved MapLibre basemap from <c>GET /api/map/style</c> (geo-tiles-plugin.md §2). Exactly one of
/// <see cref="StyleUrl"/> / <see cref="StyleJson"/> is set; the browser feeds it straight to MapLibre GL JS.
/// </summary>
public sealed record MapStyleDto(
    string? StyleUrl,
    string? StyleJson,
    string Attribution,
    string TileKind,
    bool SupportsOffline);

/// <summary>
/// Body for an event write (ARCHITECTURE.md §8 write queue / §10). Used for both
/// <c>POST /api/events</c> (create — <see cref="CalendarId"/> set) and <c>PATCH /api/events/{id}</c>
/// (edit — <see cref="CalendarId"/> ignored). Times are UTC.
/// </summary>
public sealed record EventWriteBody(
    Guid CalendarId,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    string? Location,
    string? Rrule,
    string[]? Categories);

/// <summary>
/// The host's response to a write (<c>POST/PATCH/DELETE /api/events</c>). <see cref="Queued"/> is true when
/// the edit was parked in the offline outbox to replay later rather than applied to the provider (§8).
/// </summary>
public sealed record EventWriteResultDto(
    Guid Id,
    Guid CalendarId,
    string Disposition,
    bool Queued,
    Guid? OutboxId);

/// <summary>The queued-writes summary from <c>GET /api/sync/outbox</c> — drives the "↻ (N)" footer (§8).</summary>
public sealed record OutboxStatusDto(
    int Pending,
    int Failed,
    DateTimeOffset? OldestQueuedUtc);

/// <summary>
/// An active share from <c>GET /api/shares</c> / the result of <c>POST /api/shares</c> (ARCHITECTURE.md §16).
/// <see cref="Url"/> is the absolute public <c>/share/{token}.ics</c> feed the owner copies and hands out.
/// </summary>
public sealed record ShareDto(
    Guid Id,
    Guid? CalendarId,
    string Scope,
    string Token,
    string Url,
    string FeedPath,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc);

/// <summary>Request body for <c>POST /api/shares</c> (ARCHITECTURE.md §16). Scope is FullDetails|FreeBusy.</summary>
public sealed record CreateShareBody(
    Guid? CalendarId,
    string Scope,
    DateTimeOffset? ExpiresAtUtc);

// ── Travel fares: multi-month overlay + fare watches + notifications (ARCHITECTURE §14, UI-WIREFRAMES §3) ──

/// <summary>
/// The multi-month price overlay from <c>GET /api/fares/overlay</c> (travel-fares-plugin.md §10.2): a per-date
/// cheapest-price map for the visible window. <see cref="Cells"/> is empty when no provider is configured (the
/// planner then paints nothing — no overlay, no crash).
/// </summary>
public sealed record FareOverlayDto(
    string Kind,
    string Currency,
    string? Source,
    IReadOnlyList<OverlayCellDto> Cells);

/// <summary>One overlay cell: the cheapest price for <see cref="Date"/>, its source, and whether it's a stale last-known.</summary>
public sealed record OverlayCellDto(
    DateOnly Date,
    decimal Price,
    string? Source,
    bool Stale);

/// <summary>A fare watch from <c>GET /api/fares/watches</c> (travel-fares-plugin.md §10).</summary>
public sealed record FareWatchDto(
    Guid Id,
    string Kind,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int Pax,
    string Currency,
    decimal? TargetPrice,
    decimal? LastLowPrice,
    double DropThreshold,
    bool IsActive,
    string? OriginIata,
    string? DestIata,
    double? Lat,
    double? Lng,
    int? RadiusKm,
    int? Rooms);

/// <summary>One price-history point for a watch's sparkline (<c>GET /api/fares/watches/{id}/history</c>).</summary>
public sealed record FareSampleDto(
    Guid Id,
    DateTimeOffset SampledAtUtc,
    decimal Price,
    string Currency,
    string Source,
    bool IsStale);

/// <summary>Request body for <c>POST /api/fares/watches</c>.</summary>
public sealed record CreateFareWatchBody(
    string Kind,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int? Pax = null,
    string? Currency = null,
    decimal? TargetPrice = null,
    double? DropThreshold = null,
    string? OriginIata = null,
    string? DestIata = null,
    double? Lat = null,
    double? Lng = null,
    int? RadiusKm = null,
    int? Rooms = null);

/// <summary>A persisted in-app notification from <c>GET /api/notifications</c> (fare-drop / target-cross).</summary>
public sealed record NotificationDto(
    Guid Id,
    Guid? FareWatchId,
    string Kind,
    string Channel,
    DateTimeOffset CreatedAtUtc,
    string Message,
    decimal Price,
    string Currency,
    decimal? PreviousPrice,
    string? Source,
    bool Delivered);
