using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for <see cref="EventEditor"/> (UI-WIREFRAMES §1/§7). Asserts the write affordance
/// only appears for writable calendars and that the calendar picker is limited to them (ARCHITECTURE §8).
/// No network: the API client is stubbed and never hit by these render-only assertions.
/// </summary>
public class EventEditorTests : TestContext
{
    public EventEditorTests()
    {
        Services.AddSingleton(new CalendarApiClient(new HttpClient(new NoopHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        }));
    }

    private static CalendarDto Writable(string name) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, null, IsVisible: true, IsReadOnly: false);

    private static CalendarDto ReadOnly(string name) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, null, IsVisible: true, IsReadOnly: true);

    [Fact]
    public void Renders_the_editor_form_for_a_writable_calendar()
    {
        var cals = new List<CalendarDto> { Writable("Work"), ReadOnly("Holidays") };

        var cut = RenderComponent<EventEditor>(p => p.Add(x => x.Calendars, cals));

        Assert.NotNull(cut.Find("[data-testid=editor-title]"));
        Assert.NotNull(cut.Find("[data-testid=editor-save]"));
        Assert.Empty(cut.FindAll("[data-testid=editor-no-writable]"));
    }

    [Fact]
    public void Calendar_picker_lists_only_writable_calendars()
    {
        var cals = new List<CalendarDto> { Writable("Work"), Writable("Personal"), ReadOnly("Holidays") };

        var cut = RenderComponent<EventEditor>(p => p.Add(x => x.Calendars, cals));

        var options = cut.Find("[data-testid=editor-calendar]").QuerySelectorAll("option");
        Assert.Equal(2, options.Length);  // the read-only Holidays calendar is excluded.
        var labels = options.Select(o => o.TextContent.Trim()).ToArray();
        Assert.Contains("Work", labels);
        Assert.Contains("Personal", labels);
        Assert.DoesNotContain("Holidays", labels);
    }

    [Fact]
    public void Shows_a_notice_and_no_form_when_no_calendar_is_writable()
    {
        var cals = new List<CalendarDto> { ReadOnly("Holidays"), ReadOnly("Birthdays") };

        var cut = RenderComponent<EventEditor>(p => p.Add(x => x.Calendars, cals));

        Assert.NotNull(cut.Find("[data-testid=editor-no-writable]"));
        Assert.Empty(cut.FindAll("[data-testid=editor-title]"));
        Assert.Empty(cut.FindAll("[data-testid=editor-save]"));
    }

    [Fact]
    public void Edit_mode_fixes_the_calendar_and_offers_delete()
    {
        var work = Writable("Work");
        var cals = new List<CalendarDto> { work, Writable("Personal") };
        var ev = new EventDto(
            Guid.NewGuid(), work.Id, "Work", null, "Standup",
            new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
            AllDay: false, Location: "Office", IsRecurringInstance: false, MasterId: null,
            DuplicateCount: 0, Categories: Array.Empty<string>());

        var cut = RenderComponent<EventEditor>(p => p
            .Add(x => x.Calendars, cals)
            .Add(x => x.Event, ev));

        // The calendar is fixed (no picker) and a delete affordance is present in edit mode.
        Assert.NotNull(cut.Find("[data-testid=editor-calendar-fixed]"));
        Assert.Empty(cut.FindAll("[data-testid=editor-calendar]"));
        Assert.NotNull(cut.Find("[data-testid=editor-delete]"));
        Assert.Equal("Standup", cut.Find("[data-testid=editor-title]").GetAttribute("value"));
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
