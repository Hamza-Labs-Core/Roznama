using Calendar.Application.Calendars;
using Calendar.Application.Fares;
using Calendar.Application.Plugins;
using Calendar.Domain;
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

// ── Event write-back (ARCHITECTURE §8/§10; SDK-CONTRACT §4.write). calendar.write only; read-only → 409.
//    A write reflects locally (UI updates) and pushes to the bound plugin, or queues offline. ──
api.MapPost("/events", async (CreateEventBody body, IWriteService writes, CancellationToken ct) =>
{
    if (body.CalendarId == Guid.Empty)
        return Results.BadRequest(new { error = "calendarId is required" });
    try
    {
        var result = await writes.CreateEventAsync(body.CalendarId, body.ToRequest(), ct);
        return Results.Created($"/api/events/{result.EventId}", ToWriteResponse(result));
    }
    catch (ReadOnlyCalendarException ex)
    {
        return Results.Problem(title: "Calendar is read-only", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Could not create event", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

api.MapPatch("/events/{id:guid}", async (Guid id, UpdateEventBody body, IWriteService writes, CancellationToken ct) =>
{
    try
    {
        var result = await writes.UpdateEventAsync(id, body.ToRequest(), ct);
        return Results.Ok(ToWriteResponse(result));
    }
    catch (ConcurrencyConflictException ex)
    {
        return Results.Problem(title: "Write conflict", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (ReadOnlyCalendarException ex)
    {
        return Results.Problem(title: "Calendar is read-only", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Could not update event", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

api.MapDelete("/events/{id:guid}", async (Guid id, IWriteService writes, CancellationToken ct) =>
{
    try
    {
        var result = await writes.DeleteEventAsync(id, ct);
        return Results.Ok(ToWriteResponse(result));
    }
    catch (ConcurrencyConflictException ex)
    {
        return Results.Problem(title: "Write conflict", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (ReadOnlyCalendarException ex)
    {
        return Results.Problem(title: "Calendar is read-only", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(title: "Could not delete event", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
    }
});

// ── Offline write queue (ARCHITECTURE §8). Queued count/status + an explicit replay trigger. ──
api.MapGet("/sync/outbox", async (IWriteService writes, CancellationToken ct) =>
    Results.Ok(await writes.GetOutboxStatusAsync(ct)));

api.MapPost("/sync/outbox/replay", async (IWriteService writes, CancellationToken ct) =>
    Results.Ok(await writes.ReplayAsync(ct)));

// ── Map (ARCHITECTURE §7): events with a resolved place (pins) + the place catalog ──
api.MapGet("/map/events", async (DateTimeOffset from, DateTimeOffset to, IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.GetMapEventsAsync(from, to, ct)));

api.MapGet("/places", async (IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.ListPlacesAsync(ct)));

// ── Map basemap (geo-tiles-plugin.md §2): the resolved MapLibre StyleDescriptor for the selected
//    geo.tiles provider, or a bundled default style if none is installed. Single chosen basemap — not
//    aggregated. The browser feeds StyleUrl|StyleJson straight to MapLibre GL JS. ──
api.MapGet("/map/style", async (IMapStyleService tiles, CancellationToken ct) =>
{
    var style = await tiles.GetStyleAsync(ct);
    return Results.Ok(new
    {
        styleUrl = style.StyleUrl,
        styleJson = style.StyleJson,
        attribution = style.Attribution,
        tileKind = style.TileKind,
        supportsOffline = style.SupportsOffline,
    });
});

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

// ── Fares (travel-fares-plugin.md §9, ARCHITECTURE §14). Prices flights/stays THROUGH the interchangeable-
//    pricing aggregators (fan-out → dedupe → cheapest → source-tag → stale-from-history). Returns an empty
//    list (no overlay, no crash) when no provider is registered/configured. ──
api.MapGet("/fares/flights", async (
    string from, string to, DateOnly depart, DateOnly? @return, int? adults, string? cabin,
    string? currency, string? market, IFarePricingService fares, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        return Results.BadRequest(new { error = "from and to (IATA codes) are required" });
    if (!TryParseCabin(cabin, out var cabinClass))
        return Results.BadRequest(new { error = $"unknown cabin '{cabin}' (expected economy|premiumeconomy|business|first)" });

    var result = await fares.SearchFlightsAsync(
        new FlightSearchRequest(from, to, depart, @return, adults ?? 1, cabinClass, currency, market), ct);

    return Results.Ok(new
    {
        source = result.Source,
        stale = result.Stale,
        offers = result.Offers.Select(o => new
        {
            price = o.Price, currency = o.Currency,
            from = o.From, to = o.To, date = o.Date,
            carrier = o.Carrier, flightNo = o.FlightNo,
            departUtc = o.DepartUtc, arriveUtc = o.ArriveUtc,
            deepLink = o.DeepLink, source = o.Source,
            retrievedAt = o.RetrievedAt, stale = o.Stale,
        }),
    });
});

api.MapGet("/fares/stays", async (
    double lat, double lng, DateOnly checkIn, DateOnly checkOut, int? adults, int? rooms, int? radiusKm,
    string? currency, string? market, IFarePricingService fares, CancellationToken ct) =>
{
    if (checkOut <= checkIn)
        return Results.BadRequest(new { error = "checkOut must be after checkIn" });

    var result = await fares.SearchStaysAsync(
        new StaySearchRequest(lat, lng, checkIn, checkOut, adults ?? 1, rooms ?? 1, radiusKm ?? 5, currency, market), ct);

    return Results.Ok(new
    {
        source = result.Source,
        stale = result.Stale,
        offers = result.Offers.Select(o => new
        {
            pricePerNight = o.PricePerNight, priceTotal = o.PriceTotal, currency = o.Currency,
            placeLabel = o.PlaceLabel, lat = o.Lat, lng = o.Lng,
            checkIn = o.CheckIn, checkOut = o.CheckOut,
            deepLink = o.DeepLink, source = o.Source,
            retrievedAt = o.RetrievedAt, stale = o.Stale,
        }),
    });
});

// ── Sharing — management (API.md sharing). Tokens are returned to the owner here only. ──
api.MapGet("/shares", async (IShareService shares, HttpRequest http, CancellationToken ct) =>
    Results.Ok((await shares.ListAsync(ct)).Select(dto => ToShareResponse(dto, http))));

api.MapPost("/shares", async (CreateShareBody body, IShareService shares, HttpRequest http, CancellationToken ct) =>
{
    if (!TryParseScope(body.Scope, out var scope))
        return Results.BadRequest(new { error = $"unknown scope '{body.Scope}' (expected FullDetails|FreeBusy)" });

    var filter = body.Filter is { } f
        ? new ShareFilter(f.Calendars ?? Array.Empty<Guid>(), f.Categories ?? Array.Empty<Guid>())
        : null;

    if (body.CalendarId is null && (filter is null || filter.CalendarIds.Count == 0))
        return Results.BadRequest(new { error = "a share needs a calendarId or a filter with at least one calendar" });

    try
    {
        var dto = await shares.CreateAsync(
            new CreateShareRequest(body.CalendarId, filter, scope, body.ExpiresAtUtc), ct);
        var response = ToShareResponse(dto, http);
        return Results.Created($"/api/shares/{dto.Id}", response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/shares/{id:guid}", async (Guid id, IShareService shares, CancellationToken ct) =>
    await shares.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// ── Sharing — PUBLIC feed (ARCHITECTURE §16). Outside /api, no auth. Token is the capability. ──
//    404 unknown/revoked · 410 expired · 200 text/calendar otherwise.
app.MapGet("/share/{token}.ics", async (string token, IShareService shares, IEventProjectionService projection, CancellationToken ct) =>
{
    var resolved = await shares.ResolveAsync(token, ct);
    switch (resolved.Resolution)
    {
        case ShareResolution.NotFound:
            return Results.NotFound();
        case ShareResolution.Expired:
            return Results.StatusCode(StatusCodes.Status410Gone);
    }

    var share = resolved.Share!;
    // A generous, bounded window: a calendar app subscribing to the feed wants past context + future planning.
    var now = DateTimeOffset.UtcNow;
    var from = now.AddMonths(-1);
    var to = now.AddYears(1);

    var events = await projection.GetEventsForShareAsync(share.CalendarIds, share.CategoryIds, from, to, ct);
    var ics = ShareFeedSerializer.Serialize(share.FeedName, share.Scope, events);
    return Results.Text(ics, "text/calendar", System.Text.Encoding.UTF8);
});

// ── Sharing — PUBLIC minimal web view (ARCHITECTURE §16). Read-only, no auth. ──
app.MapGet("/share/{token}", async (string token, IShareService shares, IEventProjectionService projection, CancellationToken ct) =>
{
    var resolved = await shares.ResolveAsync(token, ct);
    switch (resolved.Resolution)
    {
        case ShareResolution.NotFound:
            return Results.NotFound();
        case ShareResolution.Expired:
            return Results.StatusCode(StatusCodes.Status410Gone);
    }

    var share = resolved.Share!;
    var now = DateTimeOffset.UtcNow;
    var events = await projection.GetEventsForShareAsync(
        share.CalendarIds, share.CategoryIds, now.AddMonths(-1), now.AddYears(1), ct);
    var html = ShareWebView.Render(share.FeedName, share.Scope, $"/share/{token}.ics", events);
    return Results.Text(html, "text/html", System.Text.Encoding.UTF8);
});

app.MapFallbackToFile("index.html");

app.Run();

// Map the owner-facing DTO to the wire shape, optionally with an absolute URL built from the request host.
static object ToShareResponse(ShareDto dto, HttpRequest? http = null)
{
    string url = http is null
        ? dto.FeedPath
        : $"{http.Scheme}://{http.Host}{dto.FeedPath}";
    return new
    {
        id = dto.Id,
        calendarId = dto.CalendarId,
        scope = dto.Scope.ToString(),
        token = dto.Token,
        url,
        feedPath = dto.FeedPath,
        expiresAtUtc = dto.ExpiresAtUtc,
        createdAtUtc = dto.CreatedAtUtc,
        filter = dto.Filter is null ? null : new { calendars = dto.Filter.CalendarIds, categories = dto.Filter.CategoryIds },
    };
}

static bool TryParseScope(string? scope, out ShareScope value)
{
    if (string.IsNullOrWhiteSpace(scope))
    {
        value = ShareScope.FullDetails;
        return true;
    }
    return Enum.TryParse(scope, ignoreCase: true, out value) && Enum.IsDefined(value);
}

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

// Parse the ?cabin= query (default Economy) into the SDK CabinClass; unknown ⇒ 400.
static bool TryParseCabin(string? cabin, out CabinClass cabinClass)
{
    if (string.IsNullOrWhiteSpace(cabin))
    {
        cabinClass = CabinClass.Economy;
        return true;
    }
    return Enum.TryParse(cabin, ignoreCase: true, out cabinClass) && Enum.IsDefined(cabinClass);
}

// Map a write outcome to the wire shape so the UI can reflect Applied vs Queued (ARCHITECTURE §8).
static object ToWriteResponse(EventWriteResult result) => new
{
    id = result.EventId,
    calendarId = result.CalendarId,
    disposition = result.Disposition.ToString(),
    queued = result.Disposition == WriteDisposition.Queued,
    outboxId = result.OutboxId,
};

internal record ConnectIcsRequest(string FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory);
internal record VisibilityPatch(bool IsVisible);

internal record CreateEventBody(
    Guid CalendarId, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool AllDay, string? Location, string? Rrule, string[]? Categories)
{
    public EventWriteRequest ToRequest() =>
        new(Title, StartUtc, EndUtc, AllDay, Location, Rrule, Categories);
}

internal record UpdateEventBody(
    string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool AllDay, string? Location, string? Rrule, string[]? Categories)
{
    public EventWriteRequest ToRequest() =>
        new(Title, StartUtc, EndUtc, AllDay, Location, Rrule, Categories);
}
internal record CreateShareBody(Guid? CalendarId, ShareFilterBody? Filter, string? Scope, DateTimeOffset? ExpiresAtUtc);
internal record ShareFilterBody(Guid[]? Calendars, Guid[]? Categories);

/// <summary>Exposed so web/integration tests can drive the host via WebApplicationFactory.</summary>
public partial class Program;
