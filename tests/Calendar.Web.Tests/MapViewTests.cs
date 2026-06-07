using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="MapView"/> (UI-WIREFRAMES §6). The JS interop (MapLibre GL JS) itself is not
/// unit-tested — these assert the component renders its container + controls and the date-range scrubber, and
/// that it survives the data-load + interop calls under bUnit's loose JS runtime + a stubbed API.
/// </summary>
public class MapViewTests : TestContext
{
    public MapViewTests()
    {
        // Loose JS interop: import("./js/map.js") and its InvokeVoidAsync calls are recorded, not executed.
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Stub the API: a bundled-default style + no pins, so the empty state path is exercised.
        var handler = new StubHandler((req) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/map/style"))
            {
                return Json("""
                    {"styleUrl":null,"styleJson":"{\"version\":8,\"sources\":{},\"layers\":[]}",
                     "attribution":"© OpenStreetMap contributors","tileKind":"vector","supportsOffline":false}
                    """);
            }
            // /map/events → empty pin set.
            return Json("[]");
        });

        Services.AddSingleton(new CalendarApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/"),
        }));
    }

    [Fact]
    public void Renders_the_map_container_and_controls()
    {
        var cut = RenderComponent<MapView>();

        // The MapLibre canvas mount point + the attribution + pin-count bar.
        Assert.NotNull(cut.Find("[data-testid=map-canvas]"));
        Assert.NotNull(cut.Find("[data-testid=map-attribution]"));
        Assert.NotNull(cut.Find("[data-testid=map-pin-count]"));
    }

    [Fact]
    public void Renders_the_date_range_scrubber_with_two_handles()
    {
        var cut = RenderComponent<MapView>();

        Assert.NotNull(cut.Find("[data-testid=map-scrubber]"));
        Assert.NotNull(cut.Find("[data-testid=map-scrubber-from]"));
        Assert.NotNull(cut.Find("[data-testid=map-scrubber-to]"));
    }

    [Fact]
    public void Shows_the_empty_state_when_no_pins_are_loaded()
    {
        var cut = RenderComponent<MapView>();

        // After the async data load (no pins) the empty-state placeholder is shown.
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=map-empty]")));
    }

    [Fact]
    public void Imports_the_map_interop_module_and_initializes_the_map()
    {
        var cut = RenderComponent<MapView>();

        // The module import + initMap call are recorded by the loose JS runtime.
        cut.WaitForAssertion(() =>
            Assert.Contains(
                JSInterop.Invocations,
                i => i.Identifier == "import" &&
                     i.Arguments.Count == 1 &&
                     (i.Arguments[0] as string) == "./js/map.js"));
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
