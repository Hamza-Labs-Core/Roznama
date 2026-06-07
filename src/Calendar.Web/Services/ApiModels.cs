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
