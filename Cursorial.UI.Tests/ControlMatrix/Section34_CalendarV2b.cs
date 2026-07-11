using System.Globalization;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Calendar = Cursorial.UI.Controls.Calendar; // disambiguate from System.Globalization.Calendar

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C21 — Calendar v2b: DisplayMode drill-down (Month / Year / Decade). The header button drills up
// (Month→Year→Decade); a Year-mode month cell / Decade-mode year cell drills down; prev/next page by the mode's unit
// (∓1 month / ∓1 year / ∓10 years). Year/Decade cells are CalendarButtons. Dates pinned: Today=Jun 18 2026,
// DisplayDate=Jun 1 2026 (decade 2020–2029).
public sealed class Section34_CalendarV2b
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jun18 = new(2026, 6, 18);

    private static (UIHeadlessHost Host, Calendar Cal) Show()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(36, 16) });
        var cal = new Calendar
        {
            Today = Jun18,
            DisplayDate = Jun1,
            FirstDayOfWeek = DayOfWeek.Sunday,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(cal);
        host.RunUntilIdle();
        return (host, cal);
    }

    // A Release-mode button (the cells + header + prev/next) clicks only when IsPointerOver is true at mouse-up, so
    // hover (the frame-loop UpdateHover) before pressing — the established Section10/Section27 pattern.
    private static void ClickAt(UIHeadlessHost host, (int Column, int Row) p)
    {
        host.SendMouseMove(p.Column, p.Row);
        host.RunFrame();
        host.SendClick(p.Column, p.Row);
        host.RunUntilIdle();
    }

    private static CalendarButton Month(Calendar cal, int month) =>
        cal.ModeButtonForRepresentative(new DateOnly(2026, month, 1))!;

    private static CalendarButton Year(Calendar cal, int year) =>
        cal.ModeButtonForRepresentative(new DateOnly(year, 1, 1))!;

    [Fact] // C21.1: the default DisplayMode is Month and the day grid renders (v2a behavior unchanged)
    public void C21_1_DefaultIsMonth()
    {
        var (host, cal) = Show();
        using var _ = host;
        Assert.Equal(CalendarMode.Month, cal.DisplayMode);
        Assert.Equal(42, cal.DayCellCount);
        Assert.Empty(cal.ModeButtons);
    }

    [Fact] // C21.2: Year mode shows the 12 months; the header is the year
    public void C21_2_YearViewTwelveMonths()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Assert.Equal(12, cal.ModeButtons.Count);
        Assert.Equal(0, cal.DayCellCount); // the day grid is gone
        Assert.NotNull(Month(cal, 6));     // a cell per month, June present
        Assert.Equal(2026.ToString(CultureInfo.CurrentCulture), cal.HeaderTextPart!.Text);
    }

    [Fact] // C21.3: Decade mode shows the decade's years (10 in-decade + leading/trailing :inactive); header is the range
    public void C21_3_DecadeView()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Decade;
        host.RunUntilIdle();

        Assert.Equal(12, cal.ModeButtons.Count);
        Assert.False(Year(cal, 2020).IsInactive); // first in-decade year
        Assert.False(Year(cal, 2029).IsInactive); // last in-decade year
        Assert.True(Year(cal, 2019).IsInactive);  // leading fill (prior decade)
        Assert.True(Year(cal, 2030).IsInactive);  // trailing fill (next decade)
        Assert.Equal("2020-2029", cal.HeaderTextPart!.Text);
    }

    [Fact] // C21.4: clicking the header drills UP (Year→Decade); at Decade it is a no-op (the top level)
    public void C21_4_HeaderDrillsUp()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        ClickAt(host, cal.HeaderButton!.TranslateToWindow(0, 0));
        Assert.Equal(CalendarMode.Decade, cal.DisplayMode);

        ClickAt(host, cal.HeaderButton!.TranslateToWindow(0, 0)); // already at the top
        Assert.Equal(CalendarMode.Decade, cal.DisplayMode);       // no-op
    }

    [Fact] // C21.5: in Year mode, clicking a month cell drills down to Month mode on that month (no date selected)
    public void C21_5_YearCellDrillsToMonth()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        ClickAt(host, Month(cal, 9).TranslateToWindow(0, 0)); // September
        Assert.Equal(CalendarMode.Month, cal.DisplayMode);
        Assert.Equal(9, cal.DisplayDate.Month);
        Assert.Equal(2026, cal.DisplayDate.Year);
        Assert.Null(cal.SelectedDate); // drilling does not select
    }

    [Fact] // C21.6: in Decade mode, clicking a year cell drills down to Year mode for that year
    public void C21_6_DecadeCellDrillsToYear()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Decade;
        host.RunUntilIdle();

        ClickAt(host, Year(cal, 2024).TranslateToWindow(0, 0));
        Assert.Equal(CalendarMode.Year, cal.DisplayMode);
        Assert.Equal(2024, cal.DisplayDate.Year);
        Assert.Null(cal.SelectedDate);
    }

    [Fact] // C21.7: prev/next page by the mode unit — ∓1 year (Year), ∓10 years (Decade)
    public void C21_7_PrevNextPagesByModeUnit()
    {
        var (host, cal) = Show();
        using var _ = host;

        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();
        ClickAt(host, cal.NextButton!.TranslateToWindow(0, 0));
        Assert.Equal(2027, cal.DisplayDate.Year); // +1 year
        ClickAt(host, cal.PreviousButton!.TranslateToWindow(0, 0));
        ClickAt(host, cal.PreviousButton!.TranslateToWindow(0, 0));
        Assert.Equal(2025, cal.DisplayDate.Year); // −2 years total

        cal.DisplayMode = CalendarMode.Decade;
        host.RunUntilIdle();
        var baseYear = cal.DisplayDate.Year;
        ClickAt(host, cal.NextButton!.TranslateToWindow(0, 0));
        Assert.Equal(baseYear + 10, cal.DisplayDate.Year); // +10 years
    }

    [Fact] // C21.8: Year mode marks :today on Today's month and :selected on SelectedDate's month
    public void C21_8_YearTodayAndSelected()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.SelectedDate = new DateOnly(2026, 3, 10); // March — distinct from Today's month (June)
        host.RunUntilIdle();
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Assert.True(Month(cal, 6).IsToday);     // Today = Jun 18
        Assert.False(Month(cal, 6).IsSelected);
        Assert.True(Month(cal, 3).IsSelected);  // selection = Mar 10
        Assert.False(Month(cal, 3).IsToday);
    }

    [Fact] // C21.9: Decade mode marks :inactive on the out-of-decade cells, :today/:selected on the year
    public void C21_9_DecadeTodaySelectedInactive()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.SelectedDate = new DateOnly(2024, 5, 10); // a different year than Today (2026), same decade
        host.RunUntilIdle();
        cal.DisplayMode = CalendarMode.Decade;
        host.RunUntilIdle();

        Assert.True(Year(cal, 2026).IsToday);    // Today's year
        Assert.True(Year(cal, 2024).IsSelected); // selection's year
        Assert.True(Year(cal, 2019).IsInactive);
        Assert.True(Year(cal, 2030).IsInactive);
    }

    [Fact] // C21.10: a Year-mode month entirely past DisplayDateEnd is a disabled drill target
    public void C21_10_OutOfRangeMonthDisabled()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayDateEnd = new DateOnly(2026, 6, 30); // the year ends at June
        host.RunUntilIdle();
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Assert.True(Month(cal, 6).IsEnabled);   // June has selectable days
        Assert.False(Month(cal, 8).IsEnabled);  // August is wholly past the end
        Assert.True(Month(cal, 8).IsBlackout);  // ⇒ :blackout (unselectable span)
    }

    [Fact] // C21.11: in Year mode, arrows move focus among cells (±1, ±columns); PageDown pages the year
    public void C21_11_ModeKeyboardNavigation()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Month(cal, 1).Focus(); // January (index 0)
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow); // → February
        host.RunUntilIdle();
        Assert.True(Month(cal, 2).IsFocused);

        host.SendKey(Key.DownArrow); // February (idx 1) + 4 columns → June (idx 5)
        host.RunUntilIdle();
        Assert.True(Month(cal, 6).IsFocused);

        host.SendKey(Key.PageDown); // page +1 year
        host.RunUntilIdle();
        Assert.Equal(2027, cal.DisplayDate.Year);
    }

    [Fact] // C21.12: DisplayModeChanged fires on a mode change (old → new)
    public void C21_12_DisplayModeChangedEvent()
    {
        var (host, cal) = Show();
        using var _ = host;

        CalendarModeChangedEventArgs? last = null;
        cal.DisplayModeChanged += (_, e) => last = e;

        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Assert.NotNull(last);
        Assert.Equal(CalendarMode.Month, last!.OldMode);
        Assert.Equal(CalendarMode.Year, last.NewMode);
    }

    [Fact] // C21.13: a DatePicker resets its hosted Calendar to Month mode each time the drop-down opens (WPF parity)
    public void C21_13_DatePickerResetsToMonthOnOpen()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(36, 16) });
        using var _ = host;
        var picker = new DatePicker
        {
            DisplayDate = new DateOnly(2026, 6, 1),
            Width = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(picker);
        host.RunUntilIdle();

        picker.IsDropDownOpen = true;
        host.RunUntilIdle();
        picker.CalendarPart!.DisplayMode = CalendarMode.Decade; // drill down while open
        host.RunUntilIdle();

        picker.IsDropDownOpen = false;
        host.RunUntilIdle();
        picker.IsDropDownOpen = true; // reopen
        host.RunUntilIdle();

        Assert.Equal(CalendarMode.Month, picker.CalendarPart!.DisplayMode);
    }

    // ── audit regressions (CD-P2I-1 audit) ──────────────────────────────────────────────────────────────

    [Fact] // C21.14 (audit): a vertical arrow skipping a disabled cell stays in its COLUMN (not a ±1 sideways drift)
    public void C21_14_VerticalArrowSkipPreservesColumn()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.BlackoutDates = new[] { new CalendarDateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)) }; // June disabled
        host.RunUntilIdle();
        cal.DisplayMode = CalendarMode.Year;
        host.RunUntilIdle();

        Assert.False(Month(cal, 6).IsEnabled); // June (idx 5, col 1) is the directly-below cell from Feb

        Month(cal, 2).Focus(); // February (idx 1, col 1)
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // +4 → June (disabled) → must hop a FULL row to October (idx 9, col 1), not July (idx 6)
        host.RunUntilIdle();
        Assert.True(Month(cal, 10).IsFocused);  // October — column preserved
        Assert.False(Month(cal, 7).IsFocused);  // not July (the old ±1-drift bug)
    }

    [Fact] // C21.15 (audit): the Decade header clamps to representable years — a years 1–9 decade reads "1-9", not "0-9"
    public void C21_15_DecadeHeaderClampsLowEdge()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayDate = new DateOnly(5, 3, 1); // year 5 ⇒ decadeStart = 0
        cal.DisplayMode = CalendarMode.Decade;
        host.RunUntilIdle();

        Assert.Equal("1-9", cal.HeaderTextPart!.Text); // not the nonexistent "0-9"
    }
}
