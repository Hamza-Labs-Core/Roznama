using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="FareWatchPanel"/> (UI-WIREFRAMES §3, travel-fares-plugin.md §10). Asserts the
/// panel lists active watches with their latest low + sparkline, shows the empty state with no watches, and
/// surfaces a fare-drop notifications indicator from <c>GET /api/notifications</c>. The API is stubbed — no network.
/// </summary>
public class FareWatchPanelTests : TestContext
{
    private void UseApi(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        Services.AddSingleton(new CalendarApiClient(new HttpClient(new StubHandler(respond))
        {
            BaseAddress = new Uri("http://localhost/"),
        }));

    [Fact]
    public void Renders_active_watches_with_their_latest_low()
    {
        UseApi(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/fares/watches")
                return Json("""
                    [{"id":"11111111-1111-1111-1111-111111111111","kind":"Flight",
                      "rangeStart":"2026-08-12","rangeEnd":"2026-08-19","pax":1,"currency":"USD",
                      "targetPrice":400,"lastLowPrice":412,"dropThreshold":0.05,"isActive":true,
                      "originIata":"JFK","destIata":"LHR","lat":null,"lng":null,"radiusKm":null,"rooms":null}]
                    """);
            if (path.EndsWith("/history"))
                return Json("[]");
            return Json("[]"); // notifications
        });

        var cut = RenderComponent<FareWatchPanel>();

        cut.WaitForAssertion(() =>
        {
            var watches = cut.FindAll("[data-testid=fare-watch]");
            Assert.Single(watches);
            Assert.Contains("JFK→LHR", watches[0].TextContent);
            Assert.Contains("$412", cut.Find("[data-testid=fare-watch-price]").TextContent);
        });
    }

    [Fact]
    public void Shows_empty_state_with_no_watches()
    {
        UseApi(_ => Json("[]"));

        var cut = RenderComponent<FareWatchPanel>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=fare-watches-empty]")));
    }

    [Fact]
    public void Renders_a_sparkline_from_history()
    {
        UseApi(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/fares/watches")
                return Json("""
                    [{"id":"11111111-1111-1111-1111-111111111111","kind":"Flight",
                      "rangeStart":"2026-08-12","rangeEnd":"2026-08-19","pax":1,"currency":"USD",
                      "targetPrice":null,"lastLowPrice":412,"dropThreshold":0.05,"isActive":true,
                      "originIata":"JFK","destIata":"LHR","lat":null,"lng":null,"radiusKm":null,"rooms":null}]
                    """);
            if (path.EndsWith("/history"))
                return Json("""
                    [{"id":"22222222-2222-2222-2222-222222222222","sampledAtUtc":"2026-06-02T00:00:00Z","price":412,"currency":"USD","source":"duffel","isStale":false},
                     {"id":"33333333-3333-3333-3333-333333333333","sampledAtUtc":"2026-06-01T00:00:00Z","price":480,"currency":"USD","source":"duffel","isStale":false}]
                    """);
            return Json("[]");
        });

        var cut = RenderComponent<FareWatchPanel>();

        // Two points, newest (412) < oldest (480) → a downward (good) trend sparkline.
        cut.WaitForAssertion(() =>
        {
            var spark = cut.Find("[data-testid=fare-watch-spark]");
            Assert.Contains("is-down", spark.ClassName);
        });
    }

    [Fact]
    public void Notifications_indicator_shows_unread_count()
    {
        UseApi(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/notifications")
                return Json("""
                    [{"id":"44444444-4444-4444-4444-444444444444","fareWatchId":"11111111-1111-1111-1111-111111111111",
                      "kind":"FareDrop","channel":"InApp","createdAtUtc":"2026-06-05T10:00:00Z",
                      "message":"JFK→LHR fell to $412 (was $480) on duffel","price":412,"currency":"USD",
                      "previousPrice":480,"source":"duffel","delivered":true}]
                    """);
            return Json("[]");
        });

        var cut = RenderComponent<FareWatchPanel>();

        cut.WaitForAssertion(() =>
            Assert.Equal("1", cut.Find("[data-testid=notifications-count]").TextContent.Trim()));

        // Clicking the bell reveals the alert and clears the badge.
        cut.Find("[data-testid=notifications-indicator]").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("fell to", cut.Find("[data-testid=notification-item]").TextContent);
            Assert.Empty(cut.FindAll("[data-testid=notifications-count]"));
        });
    }

    [Fact]
    public void Create_form_is_present_with_a_kind_switch()
    {
        UseApi(_ => Json("[]"));

        var cut = RenderComponent<FareWatchPanel>();

        Assert.NotNull(cut.Find("[data-testid=fare-create-form]"));
        Assert.NotNull(cut.Find("[data-testid=fare-origin]"));
        Assert.NotNull(cut.Find("[data-testid=fare-create]"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
