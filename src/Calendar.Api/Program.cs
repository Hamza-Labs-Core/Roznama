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
builder.Services.AddCalendarServices();

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

app.MapFallbackToFile("index.html");

app.Run();

internal record ConnectIcsRequest(string FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory);
internal record VisibilityPatch(bool IsVisible);

/// <summary>Exposed so web/integration tests can drive the host via WebApplicationFactory.</summary>
public partial class Program;
