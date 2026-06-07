using Calendar.Application.Calendars;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Calendars;
using Calendar.Infrastructure.Hosting;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Discover plugins from <appBaseDir>/plugins (the build copies first-party bundles there).
builder.Configuration["PluginHost:Directories:0"] ??=
    Path.Combine(AppContext.BaseDirectory, "plugins");

builder.Services.AddCalendarDatabase(builder.Configuration);
builder.Services.AddPluginHost(builder.Configuration);
builder.Services.AddPluginAuth(builder.Configuration);
builder.Services.AddCalendarServices();
builder.Services.AddAggregation();

var app = builder.Build();

// Migrate + seed (device identity, built-in categories) on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
    db.Database.Migrate();
    var device = scope.ServiceProvider.GetRequiredService<DeviceProvider>();
    var deviceId = await device.GetDeviceIdAsync(CancellationToken.None);
    await CategorySeeder.EnsureAsync(db, deviceId, CancellationToken.None);
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/healthz", () => Results.Ok(new { status = "ok", sdk = SdkVersion.Current }));

// ── Plugins & capabilities (projections of the registry) ──
api.MapGet("/plugins", (IPluginRegistry registry) =>
    Results.Ok(registry.All().Select(r => new
    {
        id = r.Id, name = r.Manifest.Name, version = r.Manifest.Version,
        kind = r.Manifest.Kind.ToString(), state = r.State.ToString(),
        capabilities = r.Manifest.Capabilities, faultReason = r.FaultReason,
    })));

api.MapGet("/capabilities", (IPluginRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(kvp => new
    {
        capability = CapabilityIds.For(kvp.Key), plugins = kvp.Value,
    })));

// ── Accounts (ICS connect — no OAuth) ──
api.MapGet("/accounts", (IAccountService accounts, CancellationToken ct) => accounts.ListAsync(ct));

api.MapPost("/accounts", async (ConnectIcsRequest req, IAccountService accounts, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.FeedUrl))
        return Results.BadRequest(new { error = "feedUrl is required" });
    try
    {
        var id = await accounts.ConnectIcsAsync(
            req.FeedUrl, req.Name, req.RefreshMinutes ?? 60, req.ForceCategory, ct);
        return Results.Created($"/api/accounts/{id}", new { id });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Could not connect feed", detail: ex.Message, statusCode: 502);
    }
});

// ── Calendars ──
api.MapGet("/calendars", (ICalendarCatalog catalog, CancellationToken ct) => catalog.ListCalendarsAsync(ct));

api.MapPatch("/calendars/{id:guid}", async (Guid id, VisibilityPatch patch, ICalendarCatalog catalog, CancellationToken ct) =>
    await catalog.SetCalendarVisibilityAsync(id, patch.IsVisible, ct) ? Results.NoContent() : Results.NotFound());

// ── Categories ──
api.MapGet("/categories", (ICalendarCatalog catalog, CancellationToken ct) => catalog.ListCategoriesAsync(ct));

api.MapPatch("/categories/{id:guid}", async (Guid id, VisibilityPatch patch, ICalendarCatalog catalog, CancellationToken ct) =>
    await catalog.SetCategoryVisibilityAsync(id, patch.IsVisible, ct) ? Results.NoContent() : Results.NotFound());

// ── Events (the projected stream) ──
api.MapGet("/events", async (DateTimeOffset from, DateTimeOffset to, IEventProjectionService projection, CancellationToken ct) =>
    Results.Ok(await projection.GetEventsAsync(from, to, ct)));

// ── Map (ARCHITECTURE §7): events with a resolved place (pins) + the place catalog ──
api.MapGet("/map/events", async (DateTimeOffset from, DateTimeOffset to, IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.GetMapEventsAsync(from, to, ct)));

api.MapGet("/places", async (IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.ListPlacesAsync(ct)));

// ── Commute (geo-routing-plugin.md §4): the host-computed RouteLeg for the leg arriving at this event ──
api.MapGet("/events/{id:guid}/commute", async (Guid id, string? mode, IRouteService routes, CancellationToken ct) =>
{
    if (!TryParseMode(mode, out var travelMode))
        return Results.BadRequest(new { error = $"unknown mode '{mode}' (expected drive|transit|walk|bike)" });

    var leg = await routes.GetCommuteAsync(id, travelMode, ct);
    return leg is null
        ? Results.NotFound()       // no preceding placed event, unplaced endpoint, or no provider/cached leg.
        : Results.Ok(new
        {
            fromEventId = leg.FromEventId,
            toEventId = leg.ToEventId,
            mode = leg.Mode.ToString().ToLowerInvariant(),
            durationSec = leg.DurationSec,
            leaveByUtc = leg.LeaveByUtc,
            feasible = leg.Feasible,        // false ⇒ the planner's "you can't make it" conflict flag.
            conflict = !leg.Feasible,
            source = leg.Source,
            geometry = leg.Geometry,
            stale = leg.IsStale,
        });
});

app.MapFallbackToFile("index.html");

app.Run();

// Parse the ?mode= query (default Drive) into the SDK TravelMode; unknown ⇒ 400.
static bool TryParseMode(string? mode, out TravelMode travelMode)
{
    if (string.IsNullOrWhiteSpace(mode))
    {
        travelMode = TravelMode.Drive;
        return true;
    }
    return Enum.TryParse(mode, ignoreCase: true, out travelMode) && Enum.IsDefined(travelMode);
}

internal record ConnectIcsRequest(string FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory);
internal record VisibilityPatch(bool IsVisible);

/// <summary>Exposed so web/integration tests can drive the host via WebApplicationFactory.</summary>
public partial class Program;
