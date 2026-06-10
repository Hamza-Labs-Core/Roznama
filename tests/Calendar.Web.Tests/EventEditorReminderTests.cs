using System.Net;
using System.Text;
using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for the reminders section inside <see cref="EventEditor"/> (Phase 6 polish): edit mode
/// lists this event's reminders and can add/remove them; create mode (no event id yet) hides the section.
/// </summary>
public class EventEditorReminderTests : TestContext
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherEventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CalendarId = Guid.Parse("33333333-3333-3333-3333-333333333333");

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

    private static EventDto Existing() => new(
        EventId, CalendarId, "Work", null, "Standup",
        new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
        false, null, false, null, 0, Array.Empty<string>());

    private static IReadOnlyList<CalendarDto> Calendars() => new List<CalendarDto>
    {
        new(CalendarId, Guid.NewGuid(), "Work", null, IsVisible: true, IsReadOnly: false),
    };

    [Fact]
    public void Edit_mode_lists_only_this_events_reminders()
    {
        UseApi(req => req.RequestUri!.AbsolutePath == "/api/reminders"
            ? Json($$"""
                [{"id":"{{Guid.NewGuid()}}","eventId":"{{EventId}}","eventTitle":"Standup",
                  "eventStartUtc":"2026-07-01T09:00:00Z","leadMinutes":30,"firedAtUtc":null},
                 {"id":"{{Guid.NewGuid()}}","eventId":"{{OtherEventId}}","eventTitle":"Other",
                  "eventStartUtc":"2026-07-02T09:00:00Z","leadMinutes":10,"firedAtUtc":null}]
                """)
            : Json("[]"));

        var cut = RenderComponent<EventEditor>(p => p
            .Add(x => x.Calendars, Calendars())
            .Add(x => x.Event, Existing()));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=editor-reminder]");
            var row = Assert.Single(rows);                          // the other event's reminder is filtered out.
            Assert.Contains("30 min before", row.TextContent);
        });
    }

    [Fact]
    public void Adding_a_reminder_posts_the_lead_and_renders_the_new_row()
    {
        UseApi(req =>
        {
            if (req.Method == HttpMethod.Post)
                return Json($$"""
                    {"id":"{{Guid.NewGuid()}}","eventId":"{{EventId}}","eventTitle":"Standup",
                     "eventStartUtc":"2026-07-01T09:00:00Z","leadMinutes":60,"firedAtUtc":null}
                    """);
            return Json("[]");
        });

        var cut = RenderComponent<EventEditor>(p => p
            .Add(x => x.Calendars, Calendars())
            .Add(x => x.Event, Existing()));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=editor-reminder-add]")));

        cut.Find("[data-testid=editor-reminder-lead]").Change("60");
        cut.Find("[data-testid=editor-reminder-add]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(_requests, r =>
                r.Method == HttpMethod.Post &&
                r.RequestUri!.AbsolutePath == $"/api/events/{EventId}/reminders");
            Assert.Contains("1 h before", cut.Find("[data-testid=editor-reminder]").TextContent);
        });
    }

    [Fact]
    public void Create_mode_has_no_reminders_section()
    {
        UseApi(_ => Json("[]"));

        var cut = RenderComponent<EventEditor>(p => p.Add(x => x.Calendars, Calendars()));

        Assert.Empty(cut.FindAll("[data-testid=editor-reminders]"));
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
