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

// Hosted fare-watch poll (ARCHITECTURE §14, travel-fares-plugin.md §10). Disabled unless a FareWatchPolling
// section opts in; the manual POST /api/fares/watches/poll trigger always works regardless.
builder.Services.Configure<Calendar.Infrastructure.Fares.FareWatchPollingOptions>(
    builder.Configuration.GetSection("FareWatchPolling"));
builder.Services.AddHostedService<Calendar.Infrastructure.Fares.FareWatchPollingService>();

// Scheduled calendar-sync engine (ROADMAP Phase 3). The ticker is on by default (CalendarSyncPolling:Enabled
// opts out); per-account cadence + exponential backoff live in SyncScheduler options / SyncState rows.
builder.Services.Configure<SyncSchedulerOptions>(builder.Configuration.GetSection("SyncScheduler"));
builder.Services.Configure<CalendarSyncPollingOptions>(builder.Configuration.GetSection("CalendarSyncPolling"));
builder.Services.AddHostedService<CalendarSyncPollingService>();

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
        authScheme = r.Manifest.Auth.Scheme.ToString(),   // drives the "Add account" UI per provider
    })));

api.MapGet("/capabilities", (IPluginRegistry registry) =>
    Results.Ok(registry.Snapshot().Select(kvp => new
    {
        capability = CapabilityIds.For(kvp.Key), plugins = kvp.Value,
    })));

// ── Accounts (API.md "accounts — connect flow"). A bare feedUrl keeps the zero-OAuth ICS fast path;
//    any other pluginId runs the manifest's declared auth scheme: scheme-none and credentialed plugins
//    (CalDAV app-password, API keys) connect directly, OAuth plugins return an authChallenge the UI
//    redirects to, finishing at the shared callback below. ──
api.MapGet("/accounts", (IAccountService accounts, CancellationToken ct) => accounts.ListAsync(ct));

api.MapPost("/accounts", async (
    ConnectAccountBody req, IAccountService accounts, IAccountConnectService connect,
    HttpRequest http, CancellationToken ct) =>
{
    const string icsPluginId = "org.unifiedcalendar.ics";
    if (!string.IsNullOrWhiteSpace(req.FeedUrl) &&
        (string.IsNullOrWhiteSpace(req.PluginId) || req.PluginId == icsPluginId))
    {
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
    }

    if (string.IsNullOrWhiteSpace(req.PluginId))
        return Results.BadRequest(new { error = "pluginId (or feedUrl) is required" });

    try
    {
        var fallbackRedirect = $"{http.Scheme}://{http.Host}/api/accounts/oauth/callback";
        var outcome = await connect.BeginConnectAsync(
            new ConnectAccountRequest(req.PluginId, req.Name, req.Config), fallbackRedirect, ct);
        return outcome.Challenge is { } challenge
            ? Results.Ok(new
            {
                id = outcome.AccountId,
                authChallenge = new { redirectUrl = challenge.RedirectUrl, state = challenge.State },
            })
            : Results.Created($"/api/accounts/{outcome.AccountId}", new { id = outcome.AccountId });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// One callback serves every OAuth provider: the opaque state routes to its pending connect. The browser
// lands here from the provider, so respond with redirects back into the app shell, not JSON.
api.MapGet("/accounts/oauth/callback", async (
    string? state, string? code, string? error, IAccountConnectService connect, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(state))
        return Results.BadRequest(new { error = "missing state" });

    if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
    {
        await connect.CancelOAuthAsync(state, ct);
        return Results.Redirect("/?connect=denied");
    }

    try
    {
        await connect.CompleteOAuthAsync(state, code, ct);
        return Results.Redirect("/?connect=ok");
    }
    catch (Exception ex)
    {
        return Results.Redirect($"/?connect=failed&reason={Uri.EscapeDataString(ex.Message)}");
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

// ── Scheduled sync engine (ROADMAP Phase 3). Per-account cadence/backoff/health + manual triggers; the
//    hosted ticker runs the same sweep on an interval. ──
api.MapGet("/sync/status", async (ISyncScheduler scheduler, CancellationToken ct) =>
    Results.Ok(await scheduler.GetStatusAsync(ct)));

api.MapPost("/sync/run", async (bool? force, ISyncScheduler scheduler, CancellationToken ct) =>
    Results.Ok(await scheduler.SweepAsync(force ?? false, ct)));

api.MapPost("/accounts/{id:guid}/sync", async (Guid id, ISyncScheduler scheduler, CancellationToken ct) =>
{
    try
    {
        var summary = await scheduler.SyncNowAsync(id, ct);
        return summary is null ? Results.NotFound() : Results.Ok(summary);
    }
    catch (Exception ex)
    {
        // Backoff bookkeeping is already recorded; surface the provider failure to the caller.
        return Results.Problem(title: "Sync failed", detail: ex.Message, statusCode: 502);
    }
});

// ── Map (ARCHITECTURE §7): events with a resolved place (pins) + the place catalog ──
api.MapGet("/map/events", async (DateTimeOffset from, DateTimeOffset to, IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.GetMapEventsAsync(from, to, ct)));

api.MapGet("/places", async (IMapViewService map, CancellationToken ct) =>
    Results.Ok(await map.ListPlacesAsync(ct)));

// ── Trips as routes (ROADMAP Phase 5): Travel-category stops stitched into legs with cached road
//    geometry where a geo.route provider covers them; null geometry ⇒ the client draws a straight line. ──
api.MapGet("/map/trips", async (DateTimeOffset from, DateTimeOffset to, ITripRouteService trips, CancellationToken ct) =>
    Results.Ok(await trips.GetTripRoutesAsync(from, to, ct)));

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

// ── Multi-month price overlay (travel-fares-plugin.md §10.2, ARCHITECTURE §14). Returns a {date → cheapest
//    price} map for the visible window of one route (flight) or place (stay), through the interchangeable-pricing
//    aggregator (cached). An empty map (no provider/no offers) paints nothing — never a crash. ──
api.MapGet("/fares/overlay", async (
    string kind, DateOnly from, DateOnly to,
    string? origin, string? dest, double? lat, double? lng, int? radiusKm, int? nights,
    int? pax, string? currency, string? market, IFarePricingService fares, CancellationToken ct) =>
{
    if (!Enum.TryParse<FareOverlayKind>(kind, ignoreCase: true, out var overlayKind) || !Enum.IsDefined(overlayKind))
        return Results.BadRequest(new { error = $"unknown kind '{kind}' (expected flight|stay)" });
    if (to < from)
        return Results.BadRequest(new { error = "to must be on or after from" });

    // Bound the window so an overlay request can't fan out unboundedly (the planner shows ≤ ~6 months).
    const int maxDays = 200;
    if (to.DayNumber - from.DayNumber > maxDays)
        to = from.AddDays(maxDays);

    if (overlayKind == FareOverlayKind.Flight && (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(dest)))
        return Results.BadRequest(new { error = "a flight overlay requires origin and dest (IATA codes)" });
    if (overlayKind == FareOverlayKind.Stay && (lat is null || lng is null))
        return Results.BadRequest(new { error = "a stay overlay requires lat and lng" });

    var overlay = await fares.BuildOverlayAsync(new OverlayRequest(
        overlayKind, from, to, origin, dest, lat, lng, radiusKm ?? 5, nights ?? 1,
        pax ?? 1, currency, market), ct);

    return Results.Ok(new
    {
        kind = overlay.Kind.ToString(),
        currency = overlay.Currency,
        source = overlay.Source,
        cells = overlay.Cells
            .OrderBy(c => c.Key)
            .Select(c => new
            {
                date = c.Key,
                price = c.Value.Price,
                source = c.Value.Source,
                stale = c.Value.Stale,
            }),
    });
});

// ── Fare watches + price history + notify-on-drop (ARCHITECTURE §14, travel-fares-plugin.md §10) ──
//    Create/list/delete a watch, poll it through the *.price aggregators (sample + drop/target notify), and
//    read its price series. Manual poll trigger complements the hosted background sweep.
api.MapPost("/fares/watches", async (CreateFareWatchBody body, IFareWatchService watches, CancellationToken ct) =>
{
    if (!TryParseFareKind(body.Kind, out var kind))
        return Results.BadRequest(new { error = $"unknown kind '{body.Kind}' (expected Flight|Stay)" });

    try
    {
        var dto = await watches.CreateAsync(new CreateFareWatchRequest(
            kind, body.RangeStart, body.RangeEnd, body.Pax ?? 1, body.Currency, body.TargetPrice,
            body.DropThreshold, body.OriginIata, body.DestIata,
            body.Lat, body.Lng, body.RadiusKm, body.Rooms,
            body.OriginPlaceId, body.DestPlaceId), ct);
        return Results.Created($"/api/fares/watches/{dto.Id}", dto);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/fares/watches", async (IFareWatchService watches, CancellationToken ct) =>
    Results.Ok(await watches.ListAsync(ct)));

api.MapDelete("/fares/watches/{id:guid}", async (Guid id, IFareWatchService watches, CancellationToken ct) =>
    await watches.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

api.MapGet("/fares/watches/{id:guid}/history", async (Guid id, IFareWatchService watches, CancellationToken ct) =>
    Results.Ok(await watches.GetHistoryAsync(id, ct)));

api.MapPost("/fares/watches/poll", async (IFareWatchService watches, CancellationToken ct) =>
    Results.Ok(await watches.PollAsync(ct)));

api.MapGet("/notifications", async (int? limit, INotificationReader notifications, CancellationToken ct) =>
    Results.Ok(await notifications.ListAsync(limit ?? 100, ct)));

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

static bool TryParseFareKind(string? kind, out Calendar.Domain.FareKind value) =>
    Enum.TryParse(kind, ignoreCase: true, out value) && Enum.IsDefined(value);

// Map a write outcome to the wire shape so the UI can reflect Applied vs Queued (ARCHITECTURE §8).
static object ToWriteResponse(EventWriteResult result) => new
{
    id = result.EventId,
    calendarId = result.CalendarId,
    disposition = result.Disposition.ToString(),
    queued = result.Disposition == WriteDisposition.Queued,
    outboxId = result.OutboxId,
};

/// <summary>The wire body for <c>POST /api/accounts</c>: a bare <c>feedUrl</c> is the ICS fast path; any
/// other <c>pluginId</c> + <c>config</c> runs that plugin's declared auth scheme.</summary>
internal record ConnectAccountBody(
    string? PluginId, string? FeedUrl, string? Name, int? RefreshMinutes, string? ForceCategory,
    Dictionary<string, string>? Config);
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

/// <summary>The wire body for <c>POST /api/fares/watches</c> (travel-fares-plugin.md §10).</summary>
internal record CreateFareWatchBody(
    string? Kind,
    DateOnly RangeStart, DateOnly RangeEnd,
    int? Pax, string? Currency,
    decimal? TargetPrice, double? DropThreshold,
    string? OriginIata, string? DestIata,
    double? Lat, double? Lng, int? RadiusKm, int? Rooms,
    Guid? OriginPlaceId, Guid? DestPlaceId);

/// <summary>Exposed so web/integration tests can drive the host via WebApplicationFactory.</summary>
public partial class Program;
