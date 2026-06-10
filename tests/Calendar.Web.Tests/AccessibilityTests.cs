using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// A11y pass (ROADMAP Phase 6 remainder): event chips are keyboard buttons (focusable, Enter activates,
/// labelled), and modals dismiss on Escape. Render-level checks — visual focus styling lives in CSS.
/// </summary>
public class AccessibilityTests : TestContext
{
    public AccessibilityTests()
    {
        Services.AddSingleton(new CalendarApiClient(new HttpClient(new NoopHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        }));
    }

    private static EventDto Ev(string title) => new(
        Guid.NewGuid(), Guid.NewGuid(), "Work", null, title,
        new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
        false, "Room 1", false, null, 0, Array.Empty<string>());

    [Fact]
    public void Clickable_chips_are_focusable_labelled_buttons()
    {
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 7, 1))
            .Add(x => x.Events, new List<EventDto> { Ev("Standup") })
            .Add(x => x.OnEventClick, EventCallback.Factory.Create<EventDto>(this, _ => { })));

        var chip = cut.Find(".chip--clickable");
        Assert.Equal("button", chip.GetAttribute("role"));
        Assert.Equal("0", chip.GetAttribute("tabindex"));
        Assert.Contains("Standup", chip.GetAttribute("aria-label"));
        Assert.Contains("Room 1", chip.GetAttribute("aria-label"));
    }

    [Fact]
    public void Enter_activates_a_chip_like_a_click()
    {
        EventDto? opened = null;
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 7, 1))
            .Add(x => x.Events, new List<EventDto> { Ev("Standup") })
            .Add(x => x.OnEventClick, EventCallback.Factory.Create<EventDto>(this, ev => opened = ev)));

        cut.Find(".chip--clickable").KeyDown("Enter");

        Assert.NotNull(opened);
        Assert.Equal("Standup", opened!.Title);
    }

    [Fact]
    public void Inert_chips_are_not_in_the_tab_order()
    {
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 7, 1))
            .Add(x => x.Events, new List<EventDto> { Ev("Standup") }));   // no click handler bound

        var chip = cut.Find(".chip");
        Assert.Null(chip.GetAttribute("role"));
        Assert.Null(chip.GetAttribute("tabindex"));
    }

    [Fact]
    public void Escape_closes_the_event_editor()
    {
        var closed = false;
        var cals = new List<CalendarDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Work", null, IsVisible: true, IsReadOnly: false),
        };
        var cut = RenderComponent<EventEditor>(p => p
            .Add(x => x.Calendars, cals)
            .Add(x => x.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        cut.Find("[data-testid=event-editor]").KeyDown("Escape");

        Assert.True(closed);
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
