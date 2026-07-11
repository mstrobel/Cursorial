using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C18 — Calendar v2a: date bounds (DisplayDateStart/End) + BlackoutDates. Bounds clamp the view
// and gate selection (coercion); blackout dates are :blackout + disabled (unselectable); keyboard nav skips them and
// clamps at the bounds. (DisplayMode drill-down is v2b.) June 2026 pinned, Sunday-start.
public sealed class Section31_CalendarV2
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

    [Fact] // C18.1: DisplayDateStart / DisplayDateEnd clamp DisplayDate
    public void C18_1_BoundsClampDisplayDate()
    {
        var (host, cal) = Show();
        using var _ = host;

        cal.DisplayDateStart = new DateOnly(2026, 6, 10);
        cal.DisplayDate = new DateOnly(2026, 6, 1); // before Start ⇒ clamps up
        host.RunUntilIdle();
        Assert.True(cal.DisplayDate >= new DateOnly(2026, 6, 10));

        cal.DisplayDateEnd = new DateOnly(2026, 6, 20);
        cal.DisplayDate = new DateOnly(2026, 12, 1); // after End ⇒ clamps down
        host.RunUntilIdle();
        Assert.True(cal.DisplayDate <= new DateOnly(2026, 6, 20));
    }

    [Fact] // C18.2: an out-of-range SelectedDate coerces to null
    public void C18_2_OutOfRangeSelectionCleared()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayDateStart = new DateOnly(2026, 6, 10);
        cal.DisplayDateEnd = new DateOnly(2026, 6, 20);
        host.RunUntilIdle();

        cal.SelectedDate = new DateOnly(2026, 6, 5); // before Start
        host.RunUntilIdle();
        Assert.Null(cal.SelectedDate);

        cal.SelectedDate = new DateOnly(2026, 6, 15); // in range
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 15), cal.SelectedDate);
    }

    [Fact] // C18.3: tightening the bounds re-coerces a now-out-of-range selection to null
    public void C18_3_TighteningBoundsClearsSelection()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.SelectedDate = new DateOnly(2026, 6, 5);
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 5), cal.SelectedDate);

        cal.DisplayDateStart = new DateOnly(2026, 6, 10); // Jun 5 now out of range
        host.RunUntilIdle();
        Assert.Null(cal.SelectedDate);
    }

    [Fact] // C18.4: a blackout date is :blackout + disabled (unselectable)
    public void C18_4_BlackoutCellDisabledUnselectable()
    {
        var (host, cal) = Show();
        using var _ = host;
        var black = new DateOnly(2026, 6, 10);
        cal.BlackoutDates = new[] { new CalendarDateRange(black) };
        host.RunUntilIdle();

        var cell = cal.CellForDate(black)!;
        Assert.True(cell.IsBlackout);
        Assert.False(cell.IsEnabled);

        cal.SelectedDate = black; // can't select a blackout date
        host.RunUntilIdle();
        Assert.Null(cal.SelectedDate);
    }

    [Fact] // C18.5: setting BlackoutDates over the current selection clears it
    public void C18_5_BlackoutClearsExistingSelection()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.SelectedDate = new DateOnly(2026, 6, 12);
        host.RunUntilIdle();

        cal.BlackoutDates = new[] { new CalendarDateRange(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 14)) };
        host.RunUntilIdle();
        Assert.Null(cal.SelectedDate);
    }

    [Fact] // C18.6: IsTodayHighlighted=false suppresses the :today marker
    public void C18_6_IsTodayHighlighted()
    {
        var (host, cal) = Show();
        using var _ = host;
        Assert.True(cal.CellForDate(Jun18)!.IsToday);

        cal.IsTodayHighlighted = false;
        host.RunUntilIdle();
        Assert.False(cal.CellForDate(Jun18)!.IsToday);
    }

    [Fact] // C18.7: keyboard arrow navigation skips a blacked-out date
    public void C18_7_ArrowSkipsBlackout()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.BlackoutDates = new[] { new CalendarDateRange(new DateOnly(2026, 6, 16)) };
        cal.SelectedDate = new DateOnly(2026, 6, 15);
        host.RunUntilIdle();
        cal.CellForDate(new DateOnly(2026, 6, 15))!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow); // Jun 16 is blackout ⇒ skip to Jun 17
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 17), cal.SelectedDate);
    }

    [Fact] // C18.8: keyboard arrow clamps at the upper bound (no move past DisplayDateEnd)
    public void C18_8_ArrowClampsAtBound()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.DisplayDateEnd = new DateOnly(2026, 6, 20);
        cal.SelectedDate = new DateOnly(2026, 6, 20);
        host.RunUntilIdle();
        cal.CellForDate(new DateOnly(2026, 6, 20))!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow); // already at End ⇒ stays
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 20), cal.SelectedDate);
    }

    // ── audit regressions (CD-P2D-2 audit) ──────────────────────────────────────────────────────────────

    [Fact] // C18.9: FocusDate lands on a SELECTABLE cell (not a disabled out-of-range 1st-of-month → focus nowhere)
    public void C18_9_FocusDateSkipsDisabled()
    {
        var (host, cal) = Show(); // Today=Jun18 (in-month, selectable)
        using var _ = host;
        cal.DisplayDateStart = new DateOnly(2026, 6, 10); // Jun 1..9 disabled
        cal.SelectedDate = null;
        host.RunUntilIdle();

        cal.FocusDate(); // best target = Today (Jun 18), not the disabled Jun 1
        host.RunUntilIdle();
        Assert.True(cal.CellForDate(Jun18)!.IsFocused);
    }

    [Fact] // C18.10: a blacked-out / out-of-range "today" does not carry :today
    public void C18_10_BlackoutTodayNotHighlighted()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.BlackoutDates = new[] { new CalendarDateRange(Jun18) }; // today is blacked out
        host.RunUntilIdle();

        var cell = cal.CellForDate(Jun18)!;
        Assert.True(cell.IsBlackout);
        Assert.False(cell.IsToday); // not highlighted while blacked out
    }
}
