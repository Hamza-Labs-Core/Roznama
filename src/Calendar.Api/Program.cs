using Calendar.Application.Plugins;
using Calendar.Infrastructure.Hosting;
using Calendar.Infrastructure.Persistence;
using Calendar.Plugin.Abstractions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Default the plugin discovery directory to <contentRoot>/plugins unless configured.
builder.Configuration["PluginHost:Directories:0"] ??=
    Path.Combine(builder.Environment.ContentRootPath, "plugins");

builder.Services.AddCalendarDatabase(builder.Configuration);
builder.Services.AddPluginHost(builder.Configuration);

var app = builder.Build();

// Apply migrations on startup (single-user local host — DATA-SCHEMA §7).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CalendarDbContext>();
    db.Database.Migrate();
}

// Serve the Blazor WebAssembly client.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// ── API: thin projections of the plugin registry (API.md "Plugins & capabilities") ──
var api = app.MapGroup("/api");

api.MapGet("/healthz", () => Results.Ok(new { status = "ok", sdk = SdkVersion.Current }));

api.MapGet("/plugins", (IPluginRegistry registry) =>
    Results.Ok(registry.All().Select(r => new
    {
        id = r.Id,
        name = r.Manifest.Name,
        version = r.Manifest.Version,
        kind = r.Manifest.Kind.ToString(),
        state = r.State.ToString(),
        capabilities = r.Manifest.Capabilities,
        faultReason = r.FaultReason,
    })));

api.MapGet("/capabilities", (IPluginRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(kvp => new
    {
        capability = CapabilityIds.For(kvp.Key),
        plugins = kvp.Value,
    })));

// SPA fallback for client-side routing.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so the integration/web tests can drive the host via WebApplicationFactory.</summary>
public partial class Program;
