namespace Cursorial.UI.Controls;

/// <summary>
/// One month-or-year cell in a <see cref="Calendar"/>'s Year/Decade drill-down view (design doc §12 — the WPF
/// <c>CalendarButton</c> analog). A focusable <see cref="Button"/> showing an abbreviated month name (Year mode) or a
/// year (Decade mode); the <see cref="Calendar"/> stamps its state (<see cref="IsToday"/> ⇒ <c>:today</c>,
/// <see cref="IsSelected"/> ⇒ <c>:selected</c>, <see cref="IsInactive"/> ⇒ <c>:inactive</c> for a Decade view's
/// leading/trailing out-of-decade years, <see cref="IsBlackout"/> ⇒ <c>:blackout</c> for a fully-unselectable span)
/// and wires its <see cref="ButtonBase.Click"/> to drill down onto <see cref="RepresentativeDate"/>.
/// </summary>
public class CalendarButton : Button
{
    /// <summary>Whether this cell is the calendar's current "today" month/year (<c>:today</c>).</summary>
    public static readonly StyledProperty<bool> IsTodayProperty =
        UIProperty.Register<CalendarButton, bool>(nameof(IsToday));

    /// <summary>Whether this cell contains the selected date (<c>:selected</c>).</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        UIProperty.Register<CalendarButton, bool>(nameof(IsSelected));

    /// <summary>Whether this cell falls outside the shown decade (the Decade view's leading/trailing fill — <c>:inactive</c>).</summary>
    public static readonly StyledProperty<bool> IsInactiveProperty =
        UIProperty.Register<CalendarButton, bool>(nameof(IsInactive));

    /// <summary>Whether this cell's whole span is unselectable (out of the calendar's date range or fully blacked out — <c>:blackout</c>; the calendar also disables it).</summary>
    public static readonly StyledProperty<bool> IsBlackoutProperty =
        UIProperty.Register<CalendarButton, bool>(nameof(IsBlackout));

    static CalendarButton()
    {
        PseudoClassMapping.Register<CalendarButton>(IsTodayProperty, ":today");
        PseudoClassMapping.Register<CalendarButton>(IsSelectedProperty, ":selected");
        PseudoClassMapping.Register<CalendarButton>(IsInactiveProperty, ":inactive");
        PseudoClassMapping.Register<CalendarButton>(IsBlackoutProperty, ":blackout");
    }

    /// <summary>The first day of the month (Year mode) or year (Decade mode) this cell represents — the drill-down target.</summary>
    public DateOnly RepresentativeDate { get; internal set; }

    /// <inheritdoc cref="IsTodayProperty"/>
    public bool IsToday { get => GetValue(IsTodayProperty); set => SetValue(IsTodayProperty, value); }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    /// <inheritdoc cref="IsInactiveProperty"/>
    public bool IsInactive { get => GetValue(IsInactiveProperty); set => SetValue(IsInactiveProperty, value); }

    /// <inheritdoc cref="IsBlackoutProperty"/>
    public bool IsBlackout { get => GetValue(IsBlackoutProperty); set => SetValue(IsBlackoutProperty, value); }
}
