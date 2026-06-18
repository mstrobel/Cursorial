using System.Globalization;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C19 — DatePicker editable mode. IsEditable swaps the read-only face for a PART_EditableTextBox;
// typing a date + Enter / leaving the field parses (culture-aware) and commits to SelectedDate, reverting to the last
// value when it doesn't parse; the drop button still opens the Calendar to pick visually.
public sealed class Section32_DatePickerEditable
{
    private static (UITestHost Host, DatePicker Picker) Show(bool editable = true)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(36, 16) });
        var picker = new DatePicker
        {
            IsEditable = editable,
            DisplayDate = new DateOnly(2026, 6, 1),
            Width = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.ShowRoot(picker);
        host.RunUntilIdle();
        return (host, picker);
    }

    private static string Short(DateOnly d) => d.ToString("d", CultureInfo.CurrentCulture);

    [Fact] // C19.1: IsEditable swaps the face — the text box shows + becomes the tab stop; the display collapses
    public void C19_1_EditableSwapsFace()
    {
        var (host, dp) = Show(editable: false);
        using var _ = host;
        Assert.Equal(Visibility.Collapsed, dp.EditableTextBoxPart!.Visibility);
        Assert.True(dp.IsTabStop);

        dp.IsEditable = true;
        host.RunUntilIdle();
        Assert.Equal(Visibility.Visible, dp.EditableTextBoxPart!.Visibility);
        Assert.False(dp.IsTabStop);
    }

    [Fact] // C19.2: typing a valid date + Enter parses + commits SelectedDate
    public void C19_2_TypeAndCommit()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.EditableTextBoxPart!.Focus();
        host.RunUntilIdle();

        host.SendText("2026-06-18"); // ISO — parses under any culture
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 18), dp.SelectedDate);
    }

    [Fact] // C19.3: an unparseable entry reverts to the last value
    public void C19_3_InvalidReverts()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 1);
        host.RunUntilIdle();

        dp.EditableTextBoxPart!.Focus();
        dp.EditableTextBoxPart!.SelectAll();
        host.SendText("not a date");
        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 1), dp.SelectedDate);          // unchanged
        Assert.Equal(Short(new DateOnly(2026, 6, 1)), dp.EditableTextBoxPart!.Text); // box reverted
    }

    [Fact] // C19.4: setting SelectedDate programmatically shows it in the editable box
    public void C19_4_SelectionSyncsBox()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 18);
        host.RunUntilIdle();
        Assert.Equal(Short(new DateOnly(2026, 6, 18)), dp.EditableTextBoxPart!.Text);
    }

    [Fact] // C19.5: focusing the editable DatePicker delegates focus to the text box (so the caret publishes)
    public void C19_5_FocusDelegation()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.Focus();
        host.RunUntilIdle();
        Assert.True(dp.EditableTextBoxPart!.IsFocused);
    }

    [Fact] // C19.6: leaving the field commits the typed date
    public void C19_6_LostFocusCommits()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(36, 16) });
        using var _ = host;
        var dp = new DatePicker { IsEditable = true, DisplayDate = new DateOnly(2026, 6, 1), Width = 18 };
        var other = new Button { Content = "x" };
        var panel = new StackPanel();
        panel.Children.Add(dp);
        panel.Children.Add(other);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        dp.EditableTextBoxPart!.Focus();
        host.SendText("2026-07-04");
        host.RunUntilIdle();
        other.Focus(); // leave the field
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 7, 4), dp.SelectedDate);
    }

    [Fact] // C19.7: the drop button opens the calendar; a calendar pick commits + shows in the box
    public void C19_7_DropToCalendarPick()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.IsDropDownOpen = true;
        host.RunUntilIdle();

        var pick = new DateOnly(2026, 6, 20);
        dp.CalendarPart!.CellForDate(pick)!.Focus();
        host.RunUntilIdle();
        host.SendKey(Key.Enter); // commit the calendar's focused day
        host.RunUntilIdle();
        Assert.Equal(pick, dp.SelectedDate);
        Assert.False(dp.IsDropDownOpen);
        Assert.Equal(Short(pick), dp.EditableTextBoxPart!.Text);
    }

    [Fact] // C19.8: Escape reverts the edit to the current selection
    public void C19_8_EscapeReverts()
    {
        var (host, dp) = Show();
        using var _ = host;
        dp.SelectedDate = new DateOnly(2026, 6, 1);
        host.RunUntilIdle();
        dp.EditableTextBoxPart!.Focus();
        dp.EditableTextBoxPart!.SelectAll();
        host.SendText("2026-12-25"); // uncommitted draft
        host.RunUntilIdle();

        host.SendKey(Key.Escape); // box focused, dropdown closed ⇒ revert the draft
        host.RunUntilIdle();
        Assert.Equal(new DateOnly(2026, 6, 1), dp.SelectedDate);                       // unchanged
        Assert.Equal(Short(new DateOnly(2026, 6, 1)), dp.EditableTextBoxPart!.Text);   // reverted
    }
}
