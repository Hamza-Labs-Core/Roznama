namespace Calendar.Domain.Engines;

/// <summary>
/// The inputs the visibility pipeline needs about a single event. Decoupled from the persisted
/// <see cref="Entities.Event"/> so the rule stays a pure function the tests can drive directly.
/// </summary>
/// <param name="CalendarVisible">The owning calendar's <c>IsVisible</c>.</param>
/// <param name="CategoryVisibilities">Visibility of every category the event resolves to (empty = no categories).</param>
/// <param name="InDuplicateGroup">True when the event is a member of a duplicate group.</param>
/// <param name="IsCanonical">True when this event is the canonical member of its duplicate group.</param>
/// <param name="WithinSelectedRange">True when the event overlaps the currently selected view range.</param>
public readonly record struct VisibilityContext(
    bool CalendarVisible,
    IReadOnlyCollection<bool> CategoryVisibilities,
    bool InDuplicateGroup,
    bool IsCanonical,
    bool WithinSelectedRange);

/// <summary>
/// The pure visibility pipeline (ARCHITECTURE §13):
/// <code>
/// visible = calendar.IsVisible
///           AND category.All(c =&gt; c.IsVisible)
///           AND NOT (inDuplicateGroup AND not canonical)
///           AND withinSelectedRange
/// </code>
/// Suppression is a view-layer decision — nothing is deleted (ARCHITECTURE §1, §12).
/// </summary>
public static class VisibilityEvaluator
{
    /// <summary>Evaluate whether an event is visible given its <see cref="VisibilityContext"/>.</summary>
    public static bool IsVisible(in VisibilityContext ctx)
    {
        if (!ctx.CalendarVisible)
            return false;

        if (!ctx.WithinSelectedRange)
            return false;

        // A hidden category on the event hides the event ("hide all Birthdays" is one category toggle).
        foreach (var categoryVisible in ctx.CategoryVisibilities)
        {
            if (!categoryVisible)
                return false;
        }

        // Non-canonical members of a duplicate group are suppressed behind the "+N duplicates" affordance.
        if (ctx.InDuplicateGroup && !ctx.IsCanonical)
            return false;

        return true;
    }
}
