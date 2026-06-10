using System.Net;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Acceptance for the SHIPPED OSRM connector bundle (plugins/connectors/osrm) — the real plugin.yaml +
/// openapi.yaml this repo copies into &lt;out&gt;/plugins (geo-routing-plugin.md §3.1, §7). It loads through
/// the host as a running <see cref="IRouteProvider"/>, maps a canned OSRM response to a
/// <see cref="RouteResult"/>, leaves the host-computed fields (LeaveByUtc / Feasible) at their provider
/// defaults, advertises drive/walk/bike-but-no-transit coverage, and ships no public OSRM endpoint. No live
/// network — HTTP is stubbed.
/// </summary>
public sealed class OsrmConnectorBundleTests
{
    private const string BundleId = "org.unifiedcalendar.geo.osrm";

    private static string BundleDir =>
        Path.Combine(FindRepoRoot(), "plugins", "connectors", "osrm");

    [Fact]
    public async Task Shipped_bundle_loads_as_a_running_router_and_maps_a_response()
    {
        // OSRM route response: routes[0].duration in seconds (float), geometry as an encoded polyline.
        const string canned = """
            {
              "code": "Ok",
              "routes": [
                { "duration": 942.3, "distance": 5120.7, "geometry": "_p~iF~ps|U_ulLnnqC" }
              ]
            }
            """;

        var handler = new StubHandler((req, _) =>
        {
            Assert.Contains("/route/v1/driving/", req.RequestUri!.AbsolutePath);
            // lon,lat;lon,lat — longitude FIRST. from=(52.5,13.3) → "13.3,52.5", to=(52.52,13.4) → "13.4,52.52".
            Assert.Contains("13.3,52.5", req.RequestUri!.AbsolutePath);
            Assert.Contains("geometries=polyline", req.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(canned, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await LoadShippedBundleAsync(handler);

        var reg = Assert.Single(registry.ForCapability(Capability.GeoRoute));
        Assert.Equal(BundleId, reg.Id);
        Assert.Equal(PluginState.Running, reg.State);

        var router = Assert.IsAssignableFrom<IRouteProvider>(reg.Instance.Plugin);

        // Coverage matches OSRM reality: drive/walk/bike, NO transit (geo-routing-plugin.md §9 matrix).
        Assert.True(router.Coverage.Drive);
        Assert.True(router.Coverage.Walk);
        Assert.True(router.Coverage.Bike);
        Assert.False(router.Coverage.Transit);

        var result = await router.RouteAsync(
            new GeoPoint(52.5, 13.3), new GeoPoint(52.52, 13.4),
            TravelMode.Drive, DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);

        Assert.Equal(942, result.DurationSec);             // rounded from 942.3.
        Assert.Equal("_p~iF~ps|U_ulLnnqC", result.Geometry);
        Assert.Null(result.LeaveByUtc);                    // host-computed → forced null by the connector.
        Assert.True(result.Feasible);                      // host-computed → forced true by the connector.
        Assert.Equal("osrm", result.Source);
    }

    [Fact]
    public void Shipped_bundle_ships_no_public_OSRM_endpoint()
    {
        // Self-host only (geo-routing-plugin.md §3.1): no public demo server may leak into the bundle.
        var manifest = File.ReadAllText(Path.Combine(BundleDir, "plugin.yaml"));
        var openapi = File.ReadAllText(Path.Combine(BundleDir, "openapi.yaml"));

        Assert.DoesNotContain("router.project-osrm.org", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("router.project-osrm.org", openapi, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IPluginRegistry> LoadShippedBundleAsync(StubHandler handler)
    {
        // Stage the real bundle under a temp <root>/<id> so the host discovers it as a subdirectory.
        var root = Path.Combine(Path.GetTempPath(), "calendar-osrm-bundle", Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, BundleId);
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(BundleDir))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);

        var registry = new PluginRegistry();
        var host = new PluginHost(
            new YamlManifestReader(),
            new ManifestValidator(),
            new AssemblyPluginLoader(),
            new ConnectorEngine(),
            registry,
            NullLoggerFactory.Instance,
            Options.Create(new PluginHostOptions { Directories = { root } }),
            authBrokerFactory: null,
            clientFactory: _ => new HttpClient(handler) { BaseAddress = null });

        await host.LoadAllAsync(CancellationToken.None);
        return registry;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Calendar.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Calendar.sln not found above the test base directory.");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request, ct));
    }
}
