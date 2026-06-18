namespace Cursorial.UI.Controls;

/// <summary>
/// One day cell in a <see cref="Calendar"/> month grid (design doc §12 — the WPF <c>CalendarDayButton</c> analog).
/// A focusable <see cref="Button"/> showing the day number; the <see cref="Calendar"/> stamps its state
/// (<see cref="IsToday"/> ⇒ <c>:today</c>, <see cref="IsSelected"/> ⇒ <c>:selected</c>, <see cref="IsInactive"/> ⇒
/// <c>:inactive</c> for the leading/trailing days of the adjacent months) and wires its <see cref="ButtonBase.Click"/>
/// to select <see cref="Date"/>.
/// </summary>
public class CalendarDayButton : Button
{
    /// <summary>Whether this cell is the calendar's <see cref="Calendar.Today"/> (<c>:today</c>).</summary>
    public static readonly StyledProperty<bool> IsTodayProperty =
        UIProperty.Register<CalendarDayButton, bool>(nameof(IsToday));

    /// <summary>Whether this cell is the selected date (<c>:selected</c>).</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<CalendarDayButton, bool>(nameof(IsSelected));

    /// <summary>Whether this cell belongs to the previous/next month (the grid's leading/trailing fill — <c>:inactive</c>).</summary>
    public static readonly StyledProperty<bool> IsInactiveProperty =
        UIProperty.Register<CalendarDayButton, bool>(nameof(IsInactive));

    static CalendarDayButton()
    {
        PseudoClassMapping.Register<CalendarDayButton>(IsTodayProperty, ":today");
        PseudoClassMapping.Register<CalendarDayButton>(IsSelectedProperty, ":selected");
        PseudoClassMapping.Register<CalendarDayButton>(IsInactiveProperty, ":inactive");
    }

    /// <summary>The calendar date this cell represents (set by the owning <see cref="Calendar"/>).</summary>
    public DateOnly Date { get; internal set; }

    /// <inheritdoc cref="IsTodayProperty"/>
    public bool IsToday { get => GetValue(IsTodayProperty); set => SetValue(IsTodayProperty, value); }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    /// <inheritdoc cref="IsInactiveProperty"/>
    public bool IsInactive { get => GetValue(IsInactiveProperty); set => SetValue(IsInactiveProperty, value); }
}
