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
