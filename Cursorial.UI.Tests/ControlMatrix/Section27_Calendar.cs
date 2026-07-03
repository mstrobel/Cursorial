using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C14 — Calendar (P2D): a month-view date picker. The grid shows DisplayDate's month (a
// culture-ordered day-of-week header + six week rows of CalendarDayButton cells); clicking/arrow-navigating sets
// SelectedDate; :today/:selected/:inactive stamp per cell. Dates are pinned (Today/DisplayDate/FirstDayOfWeek set
// explicitly) for determinism. June 2026: June 1 is a Monday, June has 30 days.
public sealed class Section27_Calendar
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jun18 = new(2026, 6, 18); // the pinned "today"

    private static (UITestHost Host, Calendar Cal) Show()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(36, 16) });
        var cal = new Calendar
        {
            Today = Jun18,
            DisplayDate = Jun1,
            FirstDayOfWeek = DayOfWeek.Sunday, // pin so the leading/trailing fill is deterministic
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(cal);
        host.RunUntilIdle();
        return (host, cal);
    }

    // A Release-mode button (the day cells + prev/next) clicks only when IsPointerOver is true at mouse-up, so hover
    // the cell (the frame-loop UpdateHover) before pressing — the established Section10 pattern.
    private static void ClickAt(UITestHost host, (int Column, int Row) p)
    {
        host.SendMouseMove(p.Column, p.Row);
        host.RunFrame();
        host.SendClick(p.Column, p.Row);
        host.RunUntilIdle();
    }

    [Fact] // C14.1: the grid renders 42 day cells; :today / :inactive (adjacent-month) stamp correctly
    public void C14_1_MonthGridAndStates()
    {
        var (host, cal) = Show();
        using var _ = host;

        Assert.Equal(42, cal.DayCellCount); // six weeks × seven days

        Assert.True(cal.CellForDate(Jun18)!.IsToday);
        Assert.False(cal.CellForDate(Jun18)!.IsInactive); // June day, active
        Assert.False(cal.CellForDate(Jun1)!.IsToday);

        // Sunday-start June 2026: the grid begins Sun May 31 (leading fill) and ends Sat Jul 11 (trailing fill).
        Assert.True(cal.CellForDate(new DateOnly(2026, 5, 31))!.IsInactive);
        Assert.True(cal.CellForDate(new DateOnly(2026, 7, 2))!.IsInactive);
    }

    [Fact] // C14.2: clicking a day selects it (SelectedDate + :selected + SelectedDateChanged)
    public void C14_2_ClickSelects()
    {
        var (host, cal) = Show();
        using var _ = host;

        CalendarSelectedDateChangedEventArgs? last = null;
        cal.SelectedDateChanged += (_, e) => last = e;

        var target = new DateOnly(2026, 6, 10);
        ClickAt(host, cal.CellForDate(target)!.TranslateToWindow(0, 0));

        Assert.Equal(target, cal.SelectedDate);
        Assert.True(cal.CellForDate(target)!.IsSelected); // the rebuilt cell carries :selected
        Assert.NotNull(last);
        Assert.Equal(target, last!.NewDate);
    }

    [Fact] // C14.3: PageUp/PageDown (and the prev/next buttons share ChangeMonth) move DisplayDate by ∓1 month
    public void C14_3_MonthNavigation()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.CellForDate(Jun18)!.Focus(); // a focused cell puts the Calendar on the key route
        host.RunUntilIdle();

        host.SendKey(Key.PageDown); // next month
        host.RunUntilIdle();
        Assert.Equal(7, cal.DisplayDate.Month);
        Assert.NotNull(cal.CellForDate(new DateOnly(2026, 7, 15))); // July grid realized

        host.SendKey(Key.PageUp); // back to June
        host.SendKey(Key.PageUp); // and May
        host.RunUntilIdle();
        Assert.Equal(5, cal.DisplayDate.Month);
    }

    [Fact] // C14.3b: the previous button click moves the view back one month (the button wiring)
    public void C14_3b_PreviousButtonClick()
    {
        var (host, cal) = Show();
        using var _ = host;

        ClickAt(host, cal.PreviousButton!.TranslateToWindow(1, 0)); // col 1 = the '<' glyph (past the button padding)
        Assert.Equal(5, cal.DisplayDate.Month); // June → May
    }

    [Fact] // C14.4: arrow keys move SelectedDate (±1 / ±7); crossing a month boundary moves DisplayDate
    public void C14_4_ArrowNavigation()
    {
        var (host, cal) = Show();
        using var _ = host;
        cal.SelectedDate = new DateOnly(2026, 6, 15);
        host.RunUntilIdle();
        cal.CellForDate(new DateOnly(2026, 6, 15))!.Focus(); // so arrows route to the Calendar
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 16), cal.SelectedDate);

        host.SendKey(Key.DownArrow); // +7
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 23), cal.SelectedDate);

        // Step to the end of June then over the boundary into July (the view follows).
        cal.SelectedDate = new DateOnly(2026, 6, 30);
        host.RunUntilIdle();
        cal.CellForDate(new DateOnly(2026, 6, 30))!.Focus();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 7, 1), cal.SelectedDate);
        Assert.Equal(7, cal.DisplayDate.Month);
    }

    [Fact] // C14.5: clicking an adjacent-month (:inactive) day selects it and moves the view to that month
    public void C14_5_ClickInactiveDayMovesView()
    {
        var (host, cal) = Show();
        using var _ = host;

        var july2 = new DateOnly(2026, 7, 2); // a trailing fill cell in the June grid
        var cell = cal.CellForDate(july2)!;
        Assert.True(cell.IsInactive);

        ClickAt(host, cell.TranslateToWindow(0, 0));

        Assert.Equal(july2, cal.SelectedDate);
        Assert.Equal(7, cal.DisplayDate.Month);                // the view moved to July
        Assert.False(cal.CellForDate(july2)!.IsInactive);      // now an active July day
    }

    [Fact] // C14.6: FirstDayOfWeek drives the leading fill — Sunday-start shows May 31, Monday-start starts at Jun 1
    public void C14_6_FirstDayOfWeekOrdering()
    {
        var (host, cal) = Show(); // Sunday start
        using var _ = host;
        Assert.NotNull(cal.CellForDate(new DateOnly(2026, 5, 31))); // Sunday leading fill present

        cal.FirstDayOfWeek = DayOfWeek.Monday;
        host.RunUntilIdle();
        // June 1 2026 is a Monday, so a Monday-start grid begins exactly at Jun 1 — no May 31 cell.
        Assert.Null(cal.CellForDate(new DateOnly(2026, 5, 31)));
        Assert.NotNull(cal.CellForDate(Jun1));
    }

    [Fact] // C14.7: setting SelectedDate programmatically to another month syncs DisplayDate and stamps :selected
    public void C14_7_ProgrammaticSelectionSyncsView()
    {
        var (host, cal) = Show();
        using var _ = host;

        var aug20 = new DateOnly(2026, 8, 20);
        cal.SelectedDate = aug20;
        host.RunUntilIdle();

        Assert.Equal(8, cal.DisplayDate.Month);
        Assert.Equal(2026, cal.DisplayDate.Year);
        Assert.True(cal.CellForDate(aug20)!.IsSelected);
    }

    [Fact] // C14.8 (audit): after a month change, the first arrow navigates within the SHOWN month — no snap-back
    public void C14_8_ArrowAfterMonthChangeStaysInView()
    {
        var (host, cal) = Show(); // no selection; Today=Jun 18; shows June
        using var _ = host;
        cal.CellForDate(Jun18)!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.PageDown); // → July; FocusDisplayMonthCell focuses Jul 1
        host.RunUntilIdle();
        Assert.Equal(7, cal.DisplayDate.Month);

        host.SendKey(Key.RightArrow); // must step Jul 1 → Jul 2, NOT yank back to a June day off Today
        host.RunUntilIdle();
        Assert.Equal(7, cal.DisplayDate.Month);          // still July
        Assert.Equal(new DateOnly(2026, 7, 2), cal.SelectedDate);
    }

    [Fact] // C14.9 (audit): clicking a day keeps keyboard focus on that day (not the prev-month button)
    public void C14_9_ClickKeepsFocusOnDay()
    {
        var (host, cal) = Show();
        using var _ = host;

        ClickAt(host, cal.CellForDate(new DateOnly(2026, 6, 19))!.TranslateToWindow(0, 0));
        Assert.Equal(new DateOnly(2026, 6, 19), cal.SelectedDate);

        // If focus had jumped to the '<' prev button, Enter would page to May. It must stay June (focus is on the day).
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(6, cal.DisplayDate.Month);
    }

    [Fact] // C14.10 (audit): the DateOnly Min/Max boundaries don't crash — render + nav are clamped
    public void C14_10_ExtremeDatesDoNotThrow()
    {
        var (host, cal) = Show();
        using var _ = host;

        cal.DisplayDate = new DateOnly(9999, 12, 1); // the loop would overflow past DateOnly.MaxValue without the clamp
        host.RunUntilIdle();
        Assert.NotNull(cal.CellForDate(new DateOnly(9999, 12, 31)));

        cal.CellForDate(new DateOnly(9999, 12, 31))!.Focus();
        host.SendKey(Key.PageDown); // ChangeMonth past Dec 9999 must clamp (no throw)
        host.RunUntilIdle();
        Assert.Equal(12, cal.DisplayDate.Month);
        Assert.Equal(9999, cal.DisplayDate.Year);

        cal.DisplayDate = new DateOnly(1, 1, 1); // the start computation would underflow without the clamp
        host.RunUntilIdle();
        Assert.NotNull(cal.CellForDate(new DateOnly(1, 1, 1)));

        cal.CellForDate(new DateOnly(1, 1, 1))!.Focus();
        host.SendKey(Key.PageUp); // ChangeMonth before Jan 0001 must clamp (no throw)
        host.RunUntilIdle();
        Assert.Equal(1, cal.DisplayDate.Month);
        Assert.Equal(1, cal.DisplayDate.Year);
    }
}
