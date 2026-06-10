using Calendar.Domain.Engines;

namespace Calendar.Domain.Tests;

public class VisibilityEvaluatorTests
{
    private static VisibilityContext Visible() =>
        new(CalendarVisible: true, CategoryVisibilities: Array.Empty<bool>(),
            InDuplicateGroup: false, IsCanonical: false, WithinSelectedRange: true);

    [Fact]
    public void Default_event_is_visible() =>
        Assert.True(VisibilityEvaluator.IsVisible(Visible()));

    [Fact]
    public void Hidden_calendar_hides_the_event() =>
        Assert.False(VisibilityEvaluator.IsVisible(Visible() with { CalendarVisible = false }));

    [Fact]
    public void Out_of_range_event_is_hidden() =>
        Assert.False(VisibilityEvaluator.IsVisible(Visible() with { WithinSelectedRange = false }));

    [Fact]
    public void A_single_hidden_category_hides_the_event() =>
        Assert.False(VisibilityEvaluator.IsVisible(
            Visible() with { CategoryVisibilities = new[] { true, false, true } }));

    [Fact]
    public void All_visible_categories_keep_the_event_visible() =>
        Assert.True(VisibilityEvaluator.IsVisible(
            Visible() with { CategoryVisibilities = new[] { true, true } }));

    [Fact]
    public void Non_canonical_duplicate_is_suppressed() =>
        Assert.False(VisibilityEvaluator.IsVisible(
            Visible() with { InDuplicateGroup = true, IsCanonical = false }));

    [Fact]
    public void Canonical_duplicate_is_visible() =>
        Assert.True(VisibilityEvaluator.IsVisible(
            Visible() with { InDuplicateGroup = true, IsCanonical = true }));
}
