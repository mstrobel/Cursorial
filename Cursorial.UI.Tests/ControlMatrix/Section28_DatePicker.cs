using System.Globalization;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C15 — DatePicker (P2E, calendar variant): a read-only date field + a drop button that opens a
// Popup hosting a Calendar (PART_Calendar); picking a day commits the date and closes. The inline variant is the
// standalone Calendar (§C14). The field-click toggle reuses Popup.KeepOpenOnAnchorPress (cf. ComboBox).
public sealed class Section28_DatePicker
{
    private static (UIHeadlessHost Host, DatePicker Picker) Show()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 16) });
        var picker = new DatePicker
        {
            DisplayDate = new DateOnly(2026, 6, 1),
            Width = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(picker);
        host.RunUntilIdle();
        return (host, picker);
    }

    [Fact] // C15.1: SelectedDate updates the field text + raises SelectedDateChanged
    public void C15_1_SelectedDateUpdatesText()
    {
        var (host, dp) = Show();
        using var _ = host;

        CalendarSelectedDateChangedEventArgs? last = null;
        dp.SelectedDateChanged += (_, e) => last = e;

        var d = new DateOnly(2026, 6, 18);
        dp.SelectedDate = d;
        host.RunUntilIdle();

        Assert.Equal(d.ToString("d", CultureInfo.CurrentCulture), dp.DisplayText);
        Assert.NotNull(last);
        Assert.Equal(d, last!.NewDate);
    }

    [Fact] // C15.2: IsDropDownOpen drives the Popup; the Calendar part is hosted
    public void C15_2_OpenDriveCalendar()
    {
        var (host, dp) = Show();
        using var _ = host;

        dp.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(dp.IsDropDownOpen);
        Assert.NotNull(dp.CalendarPart);

        dp.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.False(dp.IsDropDownOpen);
    }

    [Fact] // C15.3: a left click on the field toggles the drop-down (anchor-owned, no dismiss-then-reopen race)
    public void C15_3_ClickFieldToggles()
    {
        var (host, dp) = Show();
        using var _ = host;
        var o = dp.TranslateToWindow(0, 0);

        host.SendClick(o.Column, o.Row); // OnMouseDown toggle — fires on press, no hover needed
        host.RunUntilIdle();
        Assert.True(dp.IsDropDownOpen);

        host.SendClick(o.Column, o.Row);
        host.RunUntilIdle();
        Assert.False(dp.IsDropDownOpen);
    }

    [Fact] // C15.4: keyboard — closed Down/F4/Enter/Space opens; Escape closes
    public void C15_4_KeyboardOpenClose()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.True(dp.IsDropDownOpen);

        host.SendKey(Key.Escape);
        host.RunUntilIdle();
        Assert.False(dp.IsDropDownOpen);
    }

    [Fact] // C15.5: committing a date in the drop-down calendar (Enter on a day) commits it to the DatePicker and closes
    public void C15_5_CalendarPickCommitsAndCloses()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.IsDropDownOpen = true;
        host.RunUntilIdle();

        var pick = new DateOnly(2026, 6, 20);
        dp.CalendarPart!.CellForDate(pick)!.Focus(); // focus the day, then Enter (a CalendarDayButton routes Enter → Click → commit)
        host.RunUntilIdle();
        host.SendKey(Key.Enter);
        host.RunUntilIdle();

        Assert.Equal(pick, dp.SelectedDate);
        Assert.False(dp.IsDropDownOpen);
        Assert.Equal(pick.ToString("d", CultureInfo.CurrentCulture), dp.DisplayText);
    }

    [Fact] // C15.8 (audit): arrow-browsing inside the open calendar does NOT commit/close — only Enter commits
    public void C15_8_ArrowBrowsesWithoutClosing()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 18);
        host.RunUntilIdle();
        dp.IsDropDownOpen = true;
        host.RunUntilIdle();
        dp.CalendarPart!.CellForDate(new DateOnly(2026, 6, 18))!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow); // browse to Jun 19
        host.RunUntilIdle();
        Assert.True(dp.IsDropDownOpen);                              // still open (a browse, not a commit)
        Assert.Equal(new DateOnly(2026, 6, 18), dp.SelectedDate);   // the picker's committed value is unchanged
        Assert.Equal(new DateOnly(2026, 6, 19), dp.CalendarPart!.SelectedDate); // the calendar browsed forward

        host.SendKey(Key.Enter); // now commit the browsed date
        host.RunUntilIdle();
        Assert.False(dp.IsDropDownOpen);
        Assert.Equal(new DateOnly(2026, 6, 19), dp.SelectedDate);
    }

    [Fact] // C15.9 (audit): re-committing the already-selected day still closes (DateCommitted fires even unchanged)
    public void C15_9_RePickCurrentDayCloses()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 18);
        host.RunUntilIdle();
        dp.IsDropDownOpen = true;
        host.RunUntilIdle();
        dp.CalendarPart!.CellForDate(new DateOnly(2026, 6, 18))!.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.Enter); // re-confirm the current day — no value change, but it must still close
        host.RunUntilIdle();
        Assert.False(dp.IsDropDownOpen);
        Assert.Equal(new DateOnly(2026, 6, 18), dp.SelectedDate);
    }

    [Fact] // C15.6: the watermark shows when no date is selected; a date replaces it
    public void C15_6_Watermark()
    {
        var (host, dp) = Show();
        using var _ = host;

        dp.Watermark = "Pick a date";
        host.RunUntilIdle();
        Assert.Equal("Pick a date", dp.DisplayText);

        dp.SelectedDate = new DateOnly(2026, 1, 2);
        host.RunUntilIdle();
        Assert.NotEqual("Pick a date", dp.DisplayText);
    }

    [Fact] // C15.7 (audit): opening with a date already selected stays open (the open-time calendar sync isn't a "pick")
    public void C15_7_OpenWithPreexistingDateStaysOpen()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 18); // a date set before opening
        host.RunUntilIdle();

        dp.IsDropDownOpen = true;
        host.RunUntilIdle();

        Assert.True(dp.IsDropDownOpen);                    // did NOT commit-and-close on the open-time push
        Assert.Equal(new DateOnly(2026, 6, 18), dp.SelectedDate);
        Assert.Equal(new DateOnly(2026, 6, 18), dp.CalendarPart!.SelectedDate); // the calendar shows it
    }
}
