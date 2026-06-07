using Bunit;
using Calendar.Web.Components;

namespace Calendar.Web.Tests;

public class MonthGridTests : TestContext
{
    [Fact]
    public void Renders_seven_day_headers_and_42_cells()
    {
        var cut = RenderComponent<MonthGrid>(p => p.Add(x => x.Month, new DateOnly(2026, 6, 15)));

        Assert.Equal(7, cut.FindAll(".month-grid__dow").Count);
        Assert.Equal(42, cut.FindAll(".month-grid__cell").Count);
    }

    [Fact]
    public void Grid_is_monday_first_so_june_2026_starts_on_the_1st()
    {
        // 1 June 2026 is a Monday → the first cell of the Monday-first grid is the 1st.
        var cut = RenderComponent<MonthGrid>(p => p.Add(x => x.Month, new DateOnly(2026, 6, 1)));

        var firstDay = cut.FindAll(".month-grid__daynum")[0].TextContent.Trim();
        Assert.Equal("1", firstDay);
    }

    [Fact]
    public void Days_outside_the_month_are_muted()
    {
        // July 2026 starts on a Wednesday → the grid leads with 29/30 June, which must be muted.
        var cut = RenderComponent<MonthGrid>(p => p.Add(x => x.Month, new DateOnly(2026, 7, 1)));

        var cells = cut.FindAll(".month-grid__cell");
        Assert.Contains("month-grid__cell--muted", cells[0].ClassName);
        Assert.Equal("29", cells[0].QuerySelector(".month-grid__daynum")!.TextContent.Trim());
    }

    [Fact]
    public void Compact_mode_adds_the_modifier_class()
    {
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1))
            .Add(x => x.Compact, true));

        Assert.NotNull(cut.Find(".month-grid--compact"));
    }
}

public class MultiMonthGridTests : TestContext
{
    [Fact]
    public void Renders_one_compact_month_card_per_requested_month()
    {
        var cut = RenderComponent<MultiMonthGrid>(p => p
            .Add(x => x.Start, new DateOnly(2026, 6, 1))
            .Add(x => x.Count, 6));

        Assert.Equal(6, cut.FindAll(".multi-month__card").Count);
        Assert.Equal(6, cut.FindAll(".month-grid--compact").Count);
    }

    [Fact]
    public void Card_titles_are_consecutive_months_from_the_start()
    {
        var start = new DateOnly(2026, 6, 1);
        var cut = RenderComponent<MultiMonthGrid>(p => p
            .Add(x => x.Start, start)
            .Add(x => x.Count, 3));

        var titles = cut.FindAll(".multi-month__title").Select(e => e.TextContent.Trim()).ToArray();
        var expected = Enumerable.Range(0, 3).Select(i => start.AddMonths(i).ToString("MMMM yyyy")).ToArray();
        Assert.Equal(expected, titles);
    }

    [Fact]
    public void Renders_real_grids_not_a_placeholder()
    {
        var cut = RenderComponent<MultiMonthGrid>(p => p
            .Add(x => x.Start, new DateOnly(2026, 6, 1))
            .Add(x => x.Count, 2));

        // Two months × 42 cells = a real scaffold, no "arriving later" placeholder text.
        Assert.Equal(84, cut.FindAll(".month-grid__cell").Count);
        Assert.Empty(cut.FindAll(".placeholder"));
    }
}
