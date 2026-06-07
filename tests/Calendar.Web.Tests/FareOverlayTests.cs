using Bunit;
using Calendar.Web.Components;
using Calendar.Web.Services;

namespace Calendar.Web.Tests;

/// <summary>
/// bUnit coverage for the multi-month price overlay (UI-WIREFRAMES §3, travel-fares-plugin.md §10.2). Asserts a
/// stubbed overlay map paints price labels on the right day cells, emphasizes the cheapest date, and that an
/// empty/no map paints no overlay at all (the no-provider degradation path).
/// </summary>
public class FareOverlayTests : TestContext
{
    private static Dictionary<DateOnly, OverlayCellDto> Overlay(params (int day, decimal price)[] cells) =>
        cells.ToDictionary(
            c => new DateOnly(2026, 6, c.day),
            c => new OverlayCellDto(new DateOnly(2026, 6, c.day), c.price, "duffel", Stale: false));

    [Fact]
    public void Overlay_renders_a_price_label_per_dated_cell()
    {
        var prices = Overlay((10, 412m), (12, 388m), (15, 540m));

        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1))
            .Add(x => x.Prices, prices)
            .Add(x => x.Currency, "USD"));

        var labels = cut.FindAll("[data-testid=price-label]");
        Assert.Equal(3, labels.Count);
        Assert.Contains(labels, l => l.TextContent.Contains("412"));
        Assert.Contains(labels, l => l.TextContent.Contains("388"));
        Assert.Contains(labels, l => l.TextContent.Contains("540"));
    }

    [Fact]
    public void Cheapest_date_is_emphasized()
    {
        var prices = Overlay((10, 412m), (12, 388m), (15, 540m));

        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1))
            .Add(x => x.Prices, prices));

        // Exactly one cheapest label (the 388 cell) and one emphasized cell.
        var cheapest = cut.FindAll(".month-grid__price--cheapest");
        Assert.Single(cheapest);
        Assert.Contains("388", cheapest[0].TextContent);
        Assert.Single(cut.FindAll(".month-grid__cell--cheapest"));
    }

    [Fact]
    public void No_overlay_paints_nothing_when_map_is_null()
    {
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1)));

        Assert.Empty(cut.FindAll("[data-testid=price-label]"));
    }

    [Fact]
    public void Empty_overlay_paints_nothing()
    {
        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1))
            .Add(x => x.Prices, new Dictionary<DateOnly, OverlayCellDto>()));

        Assert.Empty(cut.FindAll("[data-testid=price-label]"));
    }

    [Fact]
    public void Stale_cell_is_marked_stale()
    {
        var prices = new Dictionary<DateOnly, OverlayCellDto>
        {
            [new DateOnly(2026, 6, 10)] = new(new DateOnly(2026, 6, 10), 412m, "duffel", Stale: true),
        };

        var cut = RenderComponent<MonthGrid>(p => p
            .Add(x => x.Month, new DateOnly(2026, 6, 1))
            .Add(x => x.Prices, prices));

        Assert.Single(cut.FindAll(".month-grid__price--stale"));
    }

    [Fact]
    public void MultiMonth_paints_the_overlay_across_every_month()
    {
        // A price in June and one in July → both compact grids show their label.
        var prices = new Dictionary<DateOnly, OverlayCellDto>
        {
            [new DateOnly(2026, 6, 10)] = new(new DateOnly(2026, 6, 10), 412m, "duffel", false),
            [new DateOnly(2026, 7, 10)] = new(new DateOnly(2026, 7, 10), 360m, "duffel", false),
        };

        var cut = RenderComponent<MultiMonthGrid>(p => p
            .Add(x => x.Start, new DateOnly(2026, 6, 1))
            .Add(x => x.Count, 2)
            .Add(x => x.Prices, prices));

        // Each compact grid renders 42 cells incl. adjacent-month days, so a date can appear in two grids; what
        // matters is that both June and July prices are painted somewhere on the planner.
        var labels = cut.FindAll("[data-testid=price-label]");
        Assert.Contains(labels, l => l.TextContent.Contains("412"));
        Assert.Contains(labels, l => l.TextContent.Contains("360"));
    }
}
