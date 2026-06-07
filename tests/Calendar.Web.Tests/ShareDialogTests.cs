using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="ShareDialog"/> (UI-WIREFRAMES §7, ARCHITECTURE §16). Asserts the dialog
/// renders the create form (scope + expiry) and lists existing shares for the calendar from a stubbed API.
/// JS clipboard interop runs in loose mode and is recorded, not executed.
/// </summary>
public class ShareDialogTests : TestContext
{
    private readonly Guid _calendarId = Guid.NewGuid();

    public ShareDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private CalendarDto Calendar() =>
        new(_calendarId, Guid.NewGuid(), "Work", null, IsVisible: true, IsReadOnly: false);

    private void StubShares(string sharesJson)
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/shares"))
                return Json(sharesJson);
            return Json("[]");
        });
        Services.AddSingleton(new CalendarApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/"),
        }));
    }

    [Fact]
    public void Renders_the_create_form_with_scope_and_expiry()
    {
        StubShares("[]");

        var cut = RenderComponent<ShareDialog>(p => p.Add(x => x.Calendar, Calendar()));

        Assert.NotNull(cut.Find("[data-testid=share-dialog]"));
        Assert.NotNull(cut.Find("[data-testid=share-scope]"));
        Assert.NotNull(cut.Find("[data-testid=share-expiry]"));
        Assert.NotNull(cut.Find("[data-testid=share-create]"));
        // FullDetails + FreeBusy scope options.
        Assert.Equal(2, cut.Find("[data-testid=share-scope]").QuerySelectorAll("option").Length);
    }

    [Fact]
    public void Shows_the_empty_state_when_there_are_no_shares()
    {
        StubShares("[]");

        var cut = RenderComponent<ShareDialog>(p => p.Add(x => x.Calendar, Calendar()));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=share-empty]")));
    }

    [Fact]
    public void Lists_existing_shares_for_the_calendar_with_url_and_revoke()
    {
        var id = Guid.NewGuid();
        StubShares($$"""
            [{"id":"{{id}}","calendarId":"{{_calendarId}}","scope":"FullDetails",
              "token":"abc123","url":"http://localhost/share/abc123.ics","feedPath":"/share/abc123.ics",
              "expiresAtUtc":null,"createdAtUtc":"2026-06-01T00:00:00Z"}]
            """);

        var cut = RenderComponent<ShareDialog>(p => p.Add(x => x.Calendar, Calendar()));

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[data-testid=share-list]"));
            Assert.Equal("http://localhost/share/abc123.ics", cut.Find("[data-testid=share-url]").GetAttribute("value"));
            Assert.NotNull(cut.Find("[data-testid=share-copy]"));
            Assert.NotNull(cut.Find("[data-testid=share-revoke]"));
        });
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
