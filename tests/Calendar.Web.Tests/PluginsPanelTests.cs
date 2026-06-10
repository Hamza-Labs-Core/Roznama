using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="PluginsPanel"/> (marketplace UI): installed plugins render with their live
/// registry state, uninstall calls the API and surfaces the 409 reason while accounts still use a plugin,
/// and the install-from-URL form posts to <c>POST /api/plugins/install</c>. The API is stubbed — no network.
/// </summary>
public class PluginsPanelTests : TestContext
{
    private readonly List<HttpRequestMessage> _requests = new();

    private void UseApi(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        Services.AddSingleton(new CalendarApiClient(new HttpClient(new StubHandler(req =>
        {
            _requests.Add(req);
            return respond(req);
        }))
        {
            BaseAddress = new Uri("http://localhost/"),
        }));

    private const string TwoPluginsJson = """
        [{"id":"org.unifiedcalendar.ics","name":"ICS Feeds","version":"1.0.0","kind":"Assembly",
          "state":"Running","capabilities":["calendar.read"],"faultReason":null,"authScheme":"None"},
         {"id":"com.broken.plugin","name":"Broken","version":"0.1.0","kind":"Assembly",
          "state":"Faulted","capabilities":[],"faultReason":"sdk-mismatch: needs 2.x","authScheme":"None"}]
        """;

    [Fact]
    public void Lists_installed_plugins_with_their_state()
    {
        UseApi(_ => Json(TwoPluginsJson));

        var cut = RenderComponent<PluginsPanel>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=plugin-row]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("ICS Feeds", rows[0].TextContent);
            Assert.NotNull(rows[0].QuerySelector(".dot--ok"));      // Running → green
            Assert.NotNull(rows[1].QuerySelector(".dot--bad"));     // Faulted → red
        });
    }

    [Fact]
    public void Uninstall_calls_the_api_and_shows_the_conflict_reason()
    {
        UseApi(req => req.Method == HttpMethod.Delete
            ? Json("""{"error":"Plugin 'org.unifiedcalendar.ics' still has connected accounts; disconnect them first."}""",
                HttpStatusCode.Conflict)
            : Json(TwoPluginsJson));

        var cut = RenderComponent<PluginsPanel>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=plugin-row]").Count));

        cut.FindAll("[data-testid=plugin-uninstall]")[0].Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_requests, r =>
                r.Method == HttpMethod.Delete &&
                r.RequestUri!.AbsolutePath == "/api/plugins/org.unifiedcalendar.ics");
            Assert.Contains("connected accounts", cut.Find("[data-testid=plugin-error]").TextContent);
        });
    }

    [Fact]
    public void Install_posts_the_url_and_reports_the_outcome()
    {
        UseApi(req => req.Method == HttpMethod.Post
            ? Json("""
                {"id":"new.plugin","name":"New Plugin","version":"2.0.0","state":"Running",
                 "trustTier":"Signed","faultReason":null}
                """)
            : Json("[]"));

        var cut = RenderComponent<PluginsPanel>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=plugin-install-toggle]")));

        cut.Find("[data-testid=plugin-install-toggle]").Click();
        cut.Find("[data-testid=plugin-install-url]").Input("https://registry.test/new.zip");
        cut.Find("[data-testid=plugin-install-go]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_requests, r =>
                r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/plugins/install");
            Assert.Contains("Installed New Plugin v2.0.0 (Signed)",
                cut.Find("[data-testid=plugin-notice]").TextContent);
        });
    }

    [Fact]
    public void A_rejected_install_surfaces_the_gate_message()
    {
        UseApi(req => req.Method == HttpMethod.Post
            ? Json("""{"error":"The bundle is not signed by a trusted publisher."}""", HttpStatusCode.BadRequest)
            : Json("[]"));

        var cut = RenderComponent<PluginsPanel>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=plugin-install-toggle]")));

        cut.Find("[data-testid=plugin-install-toggle]").Click();
        cut.Find("[data-testid=plugin-install-url]").Input("https://evil.test/bad.zip");
        cut.Find("[data-testid=plugin-install-go]").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("trusted publisher", cut.Find("[data-testid=plugin-error]").TextContent));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }
}
