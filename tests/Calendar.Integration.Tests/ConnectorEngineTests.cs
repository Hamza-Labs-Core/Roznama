using System.Net;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Acceptance for the declarative connector engine (PLUGIN-HOST.md §5, SDK-CONTRACT.md §8): a Nominatim-style
/// <c>geo.geocode</c> connector (bundled OpenAPI + JSONata map) loads as a real <see cref="IGeocoder"/>, the
/// registry serves it under <see cref="Capability.GeoGeocode"/>, and a call against a stub HTTP handler
/// returning canned JSON produces the expected <see cref="Place"/>. No live network.
/// </summary>
public sealed class ConnectorEngineTests : IDisposable
{
    private const string PluginId = "org.unifiedcalendar.nominatim";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "calendar-connector-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Declarative_geocoder_is_served_under_GeoGeocode_and_maps_the_response()
    {
        // Canned Nominatim jsonv2 search response — one hit for "Brandenburg Gate".
        const string canned = """
            [
              {
                "lat": "52.5162746",
                "lon": "13.3777041",
                "display_name": "Brandenburg Gate, Pariser Platz, Mitte, Berlin, Germany"
              }
            ]
            """;

        var handler = new StubHandler((req, _) =>
        {
            // The binder put the query into the request — assert it bound, then return canned JSON.
            Assert.Contains("/search", req.RequestUri!.AbsolutePath);
            Assert.Contains("q=Brandenburg", Uri.UnescapeDataString(req.RequestUri!.Query).Replace(" ", string.Empty));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(canned, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await BuildAndLoadAsync(WriteNominatimBundle(), handler);

        var geocoders = registry.ForCapability(Capability.GeoGeocode);
        var reg = Assert.Single(geocoders);
        Assert.Equal(PluginId, reg.Id);
        Assert.Equal(PluginState.Running, reg.State);

        var geocoder = Assert.IsAssignableFrom<IGeocoder>(reg.Instance.Plugin);
        var place = await geocoder.GeocodeAsync("Brandenburg Gate", bias: null, CancellationToken.None);

        Assert.NotNull(place);
        Assert.Equal(52.5162746, place!.Lat, precision: 6);
        Assert.Equal(13.3777041, place.Lng, precision: 6);
        Assert.Equal("Brandenburg Gate, Pariser Platz, Mitte, Berlin, Germany", place.Label);
        Assert.Equal("nominatim", place.Source); // stamped by the map
    }

    [Fact]
    public async Task Empty_result_array_maps_to_null_place()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        });

        var registry = await BuildAndLoadAsync(WriteNominatimBundle(), handler);
        var geocoder = Assert.IsAssignableFrom<IGeocoder>(
            Assert.Single(registry.ForCapability(Capability.GeoGeocode)).Instance.Plugin);

        var place = await geocoder.GeocodeAsync("nowhere at all", bias: null, CancellationToken.None);

        Assert.Null(place); // empty array → $[0] is undefined → null (the normal "no result" case)
    }

    [Fact]
    public async Task Bias_language_placeholder_is_bound_when_present_and_omitted_when_null()
    {
        string? capturedQuery = null;
        var handler = new StubHandler((req, _) =>
        {
            capturedQuery = req.RequestUri!.Query;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await BuildAndLoadAsync(WriteNominatimBundle(), handler);
        var geocoder = Assert.IsAssignableFrom<IGeocoder>(
            Assert.Single(registry.ForCapability(Capability.GeoGeocode)).Instance.Plugin);

        // With a language bias → accept-language is present.
        await geocoder.GeocodeAsync("Berlin", new GeoBias(null, null, null, "de"), CancellationToken.None);
        Assert.Contains("accept-language=de", capturedQuery);

        // Without a bias → the null placeholder is omitted, not sent empty.
        await geocoder.GeocodeAsync("Berlin", bias: null, CancellationToken.None);
        Assert.DoesNotContain("accept-language", capturedQuery);
    }

    [Fact]
    public async Task Declarative_router_maps_duration_and_forces_host_computed_fields()
    {
        // OSRM-style route response: routes[0].duration in seconds, geometry as an encoded polyline.
        const string canned = """
            {
              "code": "Ok",
              "routes": [
                { "duration": 942.3, "geometry": "_p~iF~ps|U_ulLnnqC" }
              ]
            }
            """;

        DateTimeOffset? capturedWhen = null;
        var handler = new StubHandler((req, _) =>
        {
            capturedWhen = DateTimeOffset.UtcNow;
            Assert.Contains("/route/v1/driving/", req.RequestUri!.AbsolutePath);
            // from.lng,from.lat;to.lng,to.lat got bound into the path.
            Assert.Contains("13.3,52.5", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(canned, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await BuildAndLoadAsync(WriteOsrmBundle(), handler);
        var router = Assert.IsAssignableFrom<IRouteProvider>(
            Assert.Single(registry.ForCapability(Capability.GeoRoute)).Instance.Plugin);

        var result = await router.RouteAsync(
            new GeoPoint(52.5, 13.3), new GeoPoint(52.52, 13.4),
            TravelMode.Drive, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.NotNull(capturedWhen);
        Assert.Equal(942, result.DurationSec);                 // rounded from 942.3
        Assert.Equal("_p~iF~ps|U_ulLnnqC", result.Geometry);
        Assert.Null(result.LeaveByUtc);                        // host-computed → forced null
        Assert.True(result.Feasible);                          // host-computed → forced true
        Assert.Equal("osrm", result.Source);
    }

    private string WriteOsrmBundle()
    {
        var bundleDir = Path.Combine(_root, "osrm");
        Directory.CreateDirectory(bundleDir);

        File.WriteAllText(Path.Combine(bundleDir, "openapi.yaml"), """
            openapi: 3.0.0
            info:
              title: OSRM
              version: "1.0"
            servers:
              - url: https://osrm.example.test
            paths:
              "/route/v1/driving/{coordinates}":
                get:
                  operationId: route
                  parameters:
                    - { name: coordinates, in: path, required: true, schema: { type: string } }
                  responses:
                    "200":
                      description: route
                      content:
                        application/json:
                          schema: { type: object }
            """);

        File.WriteAllText(Path.Combine(bundleDir, "plugin.yaml"), """
            id: org.unifiedcalendar.osrm
            name: OSRM Router
            version: 1.0.0
            sdkVersion: "1.x"
            kind: declarative
            openApi: openapi.yaml
            capabilities:
              - geo.route
            auth:
              scheme: none
            network:
              allow:
                - osrm.example.test
            operations:
              route:
                call: GET /route/v1/driving/{coordinates}
                path:
                  coordinates: "{q.from.lng & ',' & q.from.lat & ';' & q.to.lng & ',' & q.to.lat}"
                query:
                  overview: "full"
                map: |
                  routes[0].{
                    "DurationSec": duration,
                    "Geometry":    geometry,
                    "Source":      "osrm"
                  }
            """);

        return _root;
    }

    private string WriteNominatimBundle()
    {
        var bundleDir = Path.Combine(_root, "nominatim");
        Directory.CreateDirectory(bundleDir);

        File.WriteAllText(Path.Combine(bundleDir, "openapi.yaml"), """
            openapi: 3.0.0
            info:
              title: Nominatim
              version: "1.0"
            servers:
              - url: https://nominatim.example.test
            paths:
              /search:
                get:
                  operationId: search
                  parameters:
                    - { name: q, in: query, schema: { type: string } }
                    - { name: format, in: query, schema: { type: string } }
                  responses:
                    "200":
                      description: results
                      content:
                        application/json:
                          schema: { type: array, items: { type: object } }
            """);

        File.WriteAllText(Path.Combine(bundleDir, "plugin.yaml"), """
            id: org.unifiedcalendar.nominatim
            name: Nominatim Geocoder
            version: 1.0.0
            sdkVersion: "1.x"
            kind: declarative
            openApi: openapi.yaml
            capabilities:
              - geo.geocode
            auth:
              scheme: none
            network:
              allow:
                - nominatim.example.test
            operations:
              geocode:
                call: GET /search
                query:
                  q: "{q.query}"
                  format: "jsonv2"
                  addressdetails: "1"
                  limit: "1"
                  viewbox: "{q.bias.viewbox}"
                  accept-language: "{q.bias.lang}"
                map: |
                  $[0].{
                    "Lat":     $number(lat),
                    "Lng":     $number(lon),
                    "Label":   display_name,
                    "Address": display_name,
                    "Source":  "nominatim"
                  }
            """);

        return _root;
    }

    private static async Task<IPluginRegistry> BuildAndLoadAsync(string directory, StubHandler handler)
    {
        var registry = new PluginRegistry();
        var host = new PluginHost(
            new YamlManifestReader(),
            new ManifestValidator(),
            new AssemblyPluginLoader(),
            new ConnectorEngine(),
            registry,
            NullLoggerFactory.Instance,
            Options.Create(new PluginHostOptions { Directories = { directory } }),
            authBrokerFactory: null,
            clientFactory: _ => new HttpClient(handler) { BaseAddress = null });

        await host.LoadAllAsync(CancellationToken.None);
        return registry;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort temp cleanup
        }
    }

    /// <summary>A canned-response <see cref="HttpMessageHandler"/> — no live network leaves the test.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request, ct));
    }
}
