using System.Net;
using Calendar.Application.Plugins;
using Calendar.Infrastructure.Plugins;
using Calendar.Plugin.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Calendar.Integration.Tests;

/// <summary>
/// Acceptance for the SHIPPED Nominatim connector bundle (plugins/connectors/nominatim) — the real
/// plugin.yaml + openapi.yaml this repo copies into <out>/plugins (geo-geocoding-places-plugin.md §4, §10).
/// It loads through the host as a running <see cref="IGeocoder"/>, maps a canned jsonv2 response to a
/// <see cref="Place"/>, and a policy-compliance guard asserts no public OSM endpoint leaks into the bundle.
/// No live network — HTTP is stubbed.
/// </summary>
public sealed class NominatimConnectorBundleTests
{
    private const string BundleId = "org.unifiedcalendar.geo.nominatim";

    private static string BundleDir =>
        Path.Combine(FindRepoRoot(), "plugins", "connectors", "nominatim");

    [Fact]
    public async Task Shipped_bundle_loads_as_a_running_geocoder_and_maps_a_response()
    {
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
            Assert.Contains("/search", req.RequestUri!.AbsolutePath);
            Assert.Contains("q=Brandenburg",
                Uri.UnescapeDataString(req.RequestUri!.Query).Replace(" ", string.Empty));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(canned, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await LoadShippedBundleAsync(handler);

        var reg = Assert.Single(registry.ForCapability(Capability.GeoGeocode));
        Assert.Equal(BundleId, reg.Id);
        Assert.Equal(PluginState.Running, reg.State);

        var geocoder = Assert.IsAssignableFrom<IGeocoder>(reg.Instance.Plugin);
        var place = await geocoder.GeocodeAsync("Brandenburg Gate", bias: null, CancellationToken.None);

        Assert.NotNull(place);
        Assert.Equal(52.5162746, place!.Lat, precision: 6);
        Assert.Equal(13.3777041, place.Lng, precision: 6);
        Assert.Equal("nominatim", place.Source);
    }

    [Fact]
    public async Task Shipped_bundle_reverse_geocodes_a_coordinate()
    {
        const string canned = """
            {
              "lat": "52.5162746",
              "lon": "13.3777041",
              "display_name": "Brandenburg Gate, Berlin, Germany"
            }
            """;

        var handler = new StubHandler((req, _) =>
        {
            Assert.Contains("/reverse", req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(canned, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var registry = await LoadShippedBundleAsync(handler);
        var geocoder = Assert.IsAssignableFrom<IGeocoder>(
            Assert.Single(registry.ForCapability(Capability.GeoGeocode)).Instance.Plugin);

        var place = await geocoder.ReverseGeocodeAsync(new GeoPoint(52.5162746, 13.3777041), CancellationToken.None);

        Assert.NotNull(place);
        Assert.Equal("Brandenburg Gate, Berlin, Germany", place!.Label);
        Assert.Equal("nominatim", place.Source);
    }

    [Fact]
    public void Shipped_bundle_ships_no_public_OSM_endpoint()
    {
        // Policy compliance (geo-geocoding-places-plugin.md §4.4 / §11): no public Nominatim host may leak
        // into the manifest or the OpenAPI server url — self-host only.
        var manifest = File.ReadAllText(Path.Combine(BundleDir, "plugin.yaml"));
        var openapi = File.ReadAllText(Path.Combine(BundleDir, "openapi.yaml"));

        Assert.DoesNotContain("nominatim.openstreetmap.org", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nominatim.openstreetmap.org", openapi, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IPluginRegistry> LoadShippedBundleAsync(StubHandler handler)
    {
        // Stage the real bundle under a temp <root>/<id> so the host discovers it as a subdirectory.
        var root = Path.Combine(Path.GetTempPath(), "calendar-nominatim-bundle", Guid.NewGuid().ToString("N"));
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
