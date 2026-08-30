using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix — Calendar range / multi selection (WPF SelectionMode + SelectedDates parity). June 2026 is pinned
// for determinism (Jun 1 is a Monday; Sunday-start grid). SelectionMode drives which gestures select ranges.
public sealed class Section35_CalendarRangeSelection
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1);
    private static readonly DateOnly Jun18 = new(2026, 6, 18);

    private static (UIHeadlessHost Host, Calendar Cal) Show(CalendarSelectionMode mode)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(36, 16) });
        var cal = new Calendar
        {
            Today = Jun18,
            DisplayDate = Jun1,
            FirstDayOfWeek = DayOfWeek.Sunday,
            SelectionMode = mode,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(cal);
        host.RunUntilIdle();
        return (host, cal);
    }

    private static (int Column, int Row) At(Calendar cal, DateOnly date) => cal.CellForDate(date)!.TranslateToWindow(0, 0);

    private static void HoverClick(UIHeadlessHost host, (int Column, int Row) p, KeyModifiers mods = default)
    {
        host.SendMouseMove(p.Column, p.Row);
        host.RunFrame();
        host.SendClick(p.Column, p.Row, modifiers: mods);
        host.RunUntilIdle();
    }

    private static DateOnly D(int day) => new(2026, 6, day);

    [Fact] // Default mode is SingleDate; SelectedDates mirrors SelectedDate
    public void SingleDate_SelectedDatesMirrorsSelectedDate()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleDate);
        using var _ = host;
        Assert.Equal(CalendarSelectionMode.SingleDate, cal.SelectionMode);

        cal.SelectedDate = D(10);
        host.RunUntilIdle();
        Assert.Equal(new[] { D(10) }, cal.SelectedDates);

        cal.SelectedDate = null;
        host.RunUntilIdle();
        Assert.Empty(cal.SelectedDates);
    }

    [Fact] // SingleRange: Shift+Click extends a contiguous range from the anchor; every day in it stamps :selected
    public void SingleRange_ShiftClickExtendsRange()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        HoverClick(host, At(cal, D(10)));                       // anchor
        HoverClick(host, At(cal, D(14)), KeyModifiers.Shift);   // extend

        Assert.Equal(5, cal.SelectedDates.Count); // Jun 10..14 inclusive
        for (var day = 10; day <= 14; day++)
        {
            Assert.Contains(D(day), cal.SelectedDates);
            Assert.True(cal.CellForDate(D(day))!.IsSelected);
        }

        Assert.Equal(D(10), cal.SelectedDate); // primary stays the anchor
    }

    [Fact] // SingleRange: Shift+DownArrow extends the range by a week
    public void SingleRange_ShiftArrowExtends()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        HoverClick(host, At(cal, D(10)));
        cal.CellForDate(D(10))!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow, KeyModifiers.Shift); // +7 → 10..17
        host.RunUntilIdle();

        Assert.Equal(8, cal.SelectedDates.Count);
        Assert.Contains(D(10), cal.SelectedDates);
        Assert.Contains(D(17), cal.SelectedDates);
    }

    [Fact] // MultipleRange: Ctrl+Click adds disjoint dates; Ctrl+Click a selected date toggles it off
    public void MultipleRange_CtrlClickAddsAndToggles()
    {
        var (host, cal) = Show(CalendarSelectionMode.MultipleRange);
        using var _ = host;

        HoverClick(host, At(cal, D(5)));                        // select 5
        HoverClick(host, At(cal, D(20)), KeyModifiers.Control); // add 20 (disjoint)

        Assert.Equal(2, cal.SelectedDates.Count);
        Assert.Contains(D(5), cal.SelectedDates);
        Assert.Contains(D(20), cal.SelectedDates);

        HoverClick(host, At(cal, D(5)), KeyModifiers.Control);  // toggle 5 off
        Assert.DoesNotContain(D(5), cal.SelectedDates);
        Assert.Contains(D(20), cal.SelectedDates);
    }

    [Fact] // None: selection is disabled — a click doesn't select and a SelectedDate assignment coerces to null
    public void None_SelectionDisabled()
    {
        var (host, cal) = Show(CalendarSelectionMode.None);
        using var _ = host;

        HoverClick(host, At(cal, D(10)));
        Assert.Null(cal.SelectedDate);
        Assert.Empty(cal.SelectedDates);

        cal.SelectedDate = D(12);
        host.RunUntilIdle();
        Assert.Null(cal.SelectedDate); // coerced away in None mode
    }

    [Fact] // SelectedDatesChanged reports the added/removed delta
    public void SelectedDatesChanged_ReportsDelta()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        CalendarSelectedDatesChangedEventArgs? last = null;
        cal.SelectedDatesChanged += (_, e) => last = e;

        cal.SelectedDates.AddRange(D(3), D(6));
        host.RunUntilIdle();

        Assert.NotNull(last);
        Assert.Equal(4, last!.AddedDates.Count);
        Assert.Empty(last.RemovedDates);
        Assert.Contains(D(3), last.AddedDates);
    }

    [Fact] // SelectedDates.AddRange throws in SingleDate mode (WPF: the collection is multi-select-only)
    public void AddRange_ThrowsInSingleDate()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleDate);
        using var _ = host;
        Assert.Throws<InvalidOperationException>(() => cal.SelectedDates.AddRange(Jun1, D(3)));
    }

    [Fact] // Blackout dates within a range are excluded, and the interactive range stops before them
    public void Range_ExcludesBlackoutDates()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;
        cal.BlackoutDates = new[] { new CalendarDateRange(D(12)) };
        host.RunUntilIdle();

        HoverClick(host, At(cal, D(10)));
        HoverClick(host, At(cal, D(14)), KeyModifiers.Shift); // 10→14 crosses the blacked-out 12

        // The contiguous run stops before the blackout: 10, 11 only.
        Assert.Contains(D(10), cal.SelectedDates);
        Assert.Contains(D(11), cal.SelectedDates);
        Assert.DoesNotContain(D(12), cal.SelectedDates);
        Assert.DoesNotContain(D(14), cal.SelectedDates);
    }

    [Fact] // Mouse-drag sweeps a contiguous range (press, move with the button held, release)
    public void SingleRange_MouseDragSelectsRange()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        var a = At(cal, D(9));
        var b = At(cal, D(12));

        host.SendMouseMove(a.Column, a.Row);
        host.RunFrame();
        host.SendMouseDown(a.Column, a.Row);
        host.RunFrame();
        host.SendMouseMove(b.Column, b.Row, MouseButtons.Left); // drag with the button held
        host.RunFrame();
        host.SendMouseUp(b.Column, b.Row);
        host.RunUntilIdle();

        Assert.Equal(4, cal.SelectedDates.Count); // 9..12
        Assert.Contains(D(9), cal.SelectedDates);
        Assert.Contains(D(12), cal.SelectedDates);
    }

    [Fact] // Narrowing SelectionMode to SingleDate reduces a range to its primary date
    public void ModeNarrowing_ReducesToPrimary()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        cal.SelectedDates.AddRange(D(3), D(6));
        host.RunUntilIdle();
        Assert.Equal(4, cal.SelectedDates.Count);

        cal.SelectionMode = CalendarSelectionMode.SingleDate;
        host.RunUntilIdle();

        Assert.Single(cal.SelectedDates);
        Assert.Equal(D(3), cal.SelectedDate);
    }

    [Fact] // DisplayDateChanged fires on navigation
    public void DisplayDateChanged_Fires()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleDate);
        using var _ = host;

        CalendarDateChangedEventArgs? last = null;
        cal.DisplayDateChanged += (_, e) => last = e;

        cal.DisplayDate = new DateOnly(2026, 7, 1);
        host.RunUntilIdle();

        Assert.NotNull(last);
        Assert.Equal(new DateOnly(2026, 7, 1), last!.NewDate);
    }

    // ── keyboard-only multi-range: Ctrl+arrow moves focus without selecting, Ctrl+Space toggles the focused day ──

    private static void FocusDay(UIHeadlessHost host, Calendar cal, DateOnly date)
    {
        cal.CellForDate(date)!.Focus();
        host.RunUntilIdle();
    }

    private static void Press(UIHeadlessHost host, Key key, KeyModifiers mods = default)
    {
        host.SendKey(key, mods);
        host.RunUntilIdle();
    }

    [Fact] // Ctrl+arrow moves keyboard focus to the adjacent day WITHOUT modifying the selection
    public void MultipleRange_CtrlArrowMovesFocusWithoutSelecting()
    {
        var (host, cal) = Show(CalendarSelectionMode.MultipleRange);
        using var _ = host;

        HoverClick(host, At(cal, D(10)));                     // seed a selection at the 10th
        Assert.Equal(new[] { D(10) }, cal.SelectedDates);
        FocusDay(host, cal, D(10));

        Press(host, Key.RightArrow, KeyModifiers.Control);    // Ctrl+Right → focus 11, select nothing

        Assert.Equal(new[] { D(10) }, cal.SelectedDates);     // selection untouched
        Assert.True(cal.CellForDate(D(11))!.IsFocused);       // focus moved
        Assert.False(cal.CellForDate(D(11))!.IsSelected);

        Press(host, Key.DownArrow, KeyModifiers.Control);     // Ctrl+Down (+7) → focus 18
        Assert.Equal(new[] { D(10) }, cal.SelectedDates);
        Assert.True(cal.CellForDate(D(18))!.IsFocused);
    }

    [Fact] // Ctrl+Space toggles the focused day in/out of the selection — both spacebar wire forms (ND10)
    public void MultipleRange_CtrlSpaceTogglesFocusedDay()
    {
        var (host, cal) = Show(CalendarSelectionMode.MultipleRange);
        using var _ = host;

        FocusDay(host, cal, D(8));
        Press(host, Key.Space, KeyModifiers.Control);         // Key.Space form → toggle 8 ON
        Assert.Contains(D(8), cal.SelectedDates);
        Assert.True(cal.CellForDate(D(8))!.IsSelected);

        Press(host, Key.Space, KeyModifiers.Control);         // toggle 8 OFF
        Assert.DoesNotContain(D(8), cal.SelectedDates);

        FocusDay(host, cal, D(9));
        host.SendKey(Key.Character, KeyModifiers.Control, text: " "); // (Character," ") form
        host.RunUntilIdle();
        Assert.Contains(D(9), cal.SelectedDates);
    }

    [Fact] // The reported gap: build two DISJOINT ranges keyboard-only (Ctrl+Space anchors, Shift+arrow grows, Ctrl+arrow relocates)
    public void MultipleRange_KeyboardBuildsDisjointRanges()
    {
        var (host, cal) = Show(CalendarSelectionMode.MultipleRange);
        using var _ = host;

        // Range 1: focus 3, toggle it on (anchor), Shift+Right twice → 3..5.
        FocusDay(host, cal, D(3));
        Press(host, Key.Space, KeyModifiers.Control);
        Press(host, Key.RightArrow, KeyModifiers.Shift);
        Press(host, Key.RightArrow, KeyModifiers.Shift);
        Assert.Equal(3, cal.SelectedDates.Count);

        // Relocate focus to the 20th WITHOUT changing the selection (5 → 12 → 19 → 20).
        Press(host, Key.DownArrow, KeyModifiers.Control);
        Press(host, Key.DownArrow, KeyModifiers.Control);
        Press(host, Key.RightArrow, KeyModifiers.Control);
        Assert.Equal(3, cal.SelectedDates.Count);             // Ctrl+arrows selected nothing
        Assert.True(cal.CellForDate(D(20))!.IsFocused);

        // Range 2: toggle 20 on (new anchor), Shift+Right → 20..21.
        Press(host, Key.Space, KeyModifiers.Control);
        Press(host, Key.RightArrow, KeyModifiers.Shift);

        Assert.Equal(5, cal.SelectedDates.Count);
        foreach (var d in new[] { D(3), D(4), D(5), D(20), D(21) })
            Assert.Contains(d, cal.SelectedDates);
    }

    [Fact] // SingleDate: Ctrl+arrow moves focus only (selection frozen); a PLAIN arrow still re-selects
    public void SingleDate_CtrlArrowMovesFocusOnly_PlainArrowStillSelects()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleDate);
        using var _ = host;

        cal.SelectedDate = D(10);
        host.RunUntilIdle();
        FocusDay(host, cal, D(10));

        Press(host, Key.RightArrow, KeyModifiers.Control);    // Ctrl+Right → focus only
        Assert.Equal(D(10), cal.SelectedDate);
        Assert.True(cal.CellForDate(D(11))!.IsFocused);

        Press(host, Key.RightArrow);                          // plain Right → re-selects 12
        Assert.Equal(D(12), cal.SelectedDate);
    }

    [Fact] // None: neither Ctrl+arrow nor Ctrl+Space can select (selection stays empty)
    public void None_CtrlGesturesDoNotSelect()
    {
        var (host, cal) = Show(CalendarSelectionMode.None);
        using var _ = host;

        FocusDay(host, cal, D(10));
        Press(host, Key.RightArrow, KeyModifiers.Control);
        Press(host, Key.Space, KeyModifiers.Control);

        Assert.Null(cal.SelectedDate);
        Assert.Empty(cal.SelectedDates);
    }

    [Fact] // Ctrl+arrow across a month boundary follows the view (flips DisplayDate) and re-lands focus, selecting nothing
    public void MultipleRange_CtrlArrowCrossesMonthBoundary()
    {
        var (host, cal) = Show(CalendarSelectionMode.MultipleRange);
        using var _ = host;

        FocusDay(host, cal, D(1));                                 // Jun 1
        Press(host, Key.LeftArrow, KeyModifiers.Control);          // Ctrl+Left → May 31, view flips to May

        Assert.Equal(new DateOnly(2026, 5, 1), cal.DisplayDate);   // view followed the focus
        Assert.True(cal.CellForDate(new DateOnly(2026, 5, 31))!.IsFocused);
        Assert.Empty(cal.SelectedDates);                          // still nothing selected
    }

    [Fact] // SingleRange: Ctrl+Space selects the focused day as the (single) range — degrades sensibly, no break
    public void SingleRange_CtrlSpaceSelectsFocusedDay()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        FocusDay(host, cal, D(7));
        Press(host, Key.Space, KeyModifiers.Control);

        Assert.Equal(new[] { D(7) }, cal.SelectedDates);
        Assert.Equal(D(7), cal.SelectedDate);
    }

    [Fact] // Ctrl+Shift+arrow still EXTENDS the range (Shift wins over the Ctrl focus-only rule — no regression)
    public void SingleRange_CtrlShiftArrowStillExtends()
    {
        var (host, cal) = Show(CalendarSelectionMode.SingleRange);
        using var _ = host;

        HoverClick(host, At(cal, D(10)));
        FocusDay(host, cal, D(10));

        Press(host, Key.DownArrow, KeyModifiers.Shift | KeyModifiers.Control); // +7 → 10..17

        Assert.Equal(8, cal.SelectedDates.Count);
        Assert.Contains(D(10), cal.SelectedDates);
        Assert.Contains(D(17), cal.SelectedDates);
    }
}
