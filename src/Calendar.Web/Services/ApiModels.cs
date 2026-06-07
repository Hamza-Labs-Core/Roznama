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

public sealed record ConnectIcsRequest(string FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory);

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
