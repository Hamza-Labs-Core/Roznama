using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="DuplicatesDialog"/> + the MonthGrid "+N" badge entry point
/// (ARCHITECTURE §12): the dialog lists copies with the shown one marked, "Show this copy" posts a
/// SetCanonical override and notifies the parent, and the badge opens the inspector without also
/// opening the editor.
/// </summary>
public class DuplicatesDialogTests : TestContext
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");

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

    private static string GroupJson() => $$"""
        {"groupId":"99999999-9999-9999-9999-999999999999","members":[
          {"eventId":"{{EventId}}","uid":"uid-a","title":"New Year Party","startUtc":"2026-12-31T20:00:00Z",
           "calendarName":"Work","accountName":"Work Google","isCanonical":true},
          {"eventId":"22222222-2222-2222-2222-222222222222","uid":"uid-b","title":"New Year Party",
           "startUtc":"2026-12-31T20:00:00Z","calendarName":"Personal","accountName":"Personal","isCanonical":false}
        ]}
        """;

    [Fact]
    public void Lists_the_copies_and_marks_the_shown_one()
    {
        UseApi(_ => Json(GroupJson()));

        var cut = RenderComponent<DuplicatesDialog>(p => p.Add(x => x.EventId, EventId));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=duplicate-member]");
            Assert.Equal(2, rows.Count);
            Assert.Contains("shown", rows[0].TextContent);
            // The canonical row offers split only; the other offers both actions.
            Assert.Empty(rows[0].QuerySelectorAll("[data-testid=duplicate-make-canonical]"));
            Assert.NotNull(rows[1].QuerySelector("[data-testid=duplicate-make-canonical]"));
        });
    }

    [Fact]
    public void Show_this_copy_posts_a_SetCanonical_override_and_notifies_the_parent()
    {
        var changed = false;
        UseApi(req => req.Method == HttpMethod.Post
            ? Json("""{"id":"33333333-3333-3333-3333-333333333333","kind":"SetCanonical","uids":["uid-b"],"reason":null,"createdAtUtc":"2026-06-10T00:00:00Z"}""", HttpStatusCode.Created)
            : Json(GroupJson()));

        var cut = RenderComponent<DuplicatesDialog>(p => p
            .Add(x => x.EventId, EventId)
            .Add(x => x.OnChanged, EventCallback.Factory.Create(this, () => changed = true)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=duplicate-member]").Count));

        cut.Find("[data-testid=duplicate-make-canonical]").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_requests, r => r.Method == HttpMethod.Post);
            Assert.Equal("/api/duplicates/overrides", post.RequestUri!.AbsolutePath);
            Assert.True(changed);
        });
    }

    [Fact]
    public void The_grid_badge_opens_the_inspector_not_the_editor()
    {
        Services.AddSingleton(new CalendarApiClient(new HttpClient(new StubHandler(_ => Json("[]")))
        {
            BaseAddress = new Uri("http://localhost/"),
        }));

        EventDto? edited = null;
        EventDto? inspected = null;
        var ev = new EventDto(
            EventId, Guid.NewGuid(), "Work", null, "New Year Party",
            new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            false, null, false, null, DuplicateCount: 1, Array.Empty<string>());

        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 7, 1))
            .Add(x => x.Events, new List<EventDto> { ev })
            .Add(x => x.OnEventClick, EventCallback.Factory.Create<EventDto>(this, e => edited = e))
            .Add(x => x.OnDuplicateClick, EventCallback.Factory.Create<EventDto>(this, e => inspected = e)));

        cut.Find("[data-testid=chip-dup]").Click();

        Assert.NotNull(inspected);          // the badge opened the inspector…
        Assert.Null(edited);                // …without bubbling into the chip's edit click.
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
