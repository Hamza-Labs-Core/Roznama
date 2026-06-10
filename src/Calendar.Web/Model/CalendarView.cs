namespace Calendar.Web.Model;

/// <summary>
/// The views over the one shared event stream (UI-WIREFRAMES §1, ARCHITECTURE §6). Switching is a pure
/// client re-projection — it never re-queries providers.
/// </summary>
public enum CalendarView
{
    Month,
    MultiMonth,
    Week,
    Day,
    Agenda,
    Map
}

/// <summary>Display metadata for the segmented view switcher.</summary>
public readonly record struct ViewOption(CalendarView View, string Label, char Hotkey);

public static class CalendarViews
{
    /// <summary>The switcher order from the wireframe: Month | Multi | Week | Day | Agenda | Map.</summary>
    public static readonly IReadOnlyList<ViewOption> All = new[]
    {
        new ViewOption(CalendarView.Month, "Month", 'M'),
        new ViewOption(CalendarView.MultiMonth, "Multi", 'U'),
        new ViewOption(CalendarView.Week, "Week", 'W'),
        new ViewOption(CalendarView.Day, "Day", 'D'),
        new ViewOption(CalendarView.Agenda, "Agenda", 'A'),
        new ViewOption(CalendarView.Map, "Map", 'G'),
    };
}
