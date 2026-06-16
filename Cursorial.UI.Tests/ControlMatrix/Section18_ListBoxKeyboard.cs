using System.Collections.ObjectModel;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C5 — ListBox keyboard navigation (P9.3b: arrows/Home/End/Space/Enter + the focus-row cue).
public sealed class Section18_ListBoxKeyboard
{
    private static (UITestHost Host, ListBox List) Show(SelectionMode mode = SelectionMode.Single)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 10) });
        var lb = new ListBox { SelectionMode = mode, ItemsSource = new[] { "a", "b", "c", "d" } };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb);
    }

    private static ListBoxItem Item(ListBox lb, int index) => (ListBoxItem)lb.ItemContainerGenerator.ContainerFromIndex(index)!;

    private static void FocusItem(UITestHost host, ListBox lb, int index)
    {
        Item(lb, index).Focus();
        host.RunUntilIdle();
    }

    [Fact] // C5.1: Down moves the current + selects (single), focuses the target
    public void C5_1_Down_MovesAndSelects()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 0);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);
        Assert.True(Item(lb, 1).IsFocused);
    }

    [Fact] // C5.2: Up moves up
    public void C5_2_Up_Moves()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 2);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);
    }

    [Fact] // C5.3: Down clamps at the last item
    public void C5_3_Down_ClampsAtLast()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 3);

        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        Assert.Equal(3, lb.SelectedIndex);
    }

    [Fact] // C5.4: Up clamps at the first item
    public void C5_4_Up_ClampsAtFirst()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 0);

        host.SendKey(Key.UpArrow);
        host.RunUntilIdle();
        Assert.Equal(0, lb.SelectedIndex);
    }

    [Fact] // C5.5: Home jumps to the first
    public void C5_5_Home()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 2);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, lb.SelectedIndex);
    }

    [Fact] // C5.6: End jumps to the last
    public void C5_6_End()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 0);

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(3, lb.SelectedIndex);
    }

    [Fact] // C5.8: Multiple plain Down replaces the selection
    public void C5_8_Multiple_PlainDown_Replaces()
    {
        var (host, lb) = Show(SelectionMode.Multiple);
        using var _ = host;
        FocusItem(host, lb, 1);
        host.SendKey(Key.Space); // select 1
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow); // plain → replace with 2
        host.RunUntilIdle();
        Assert.False(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 2).IsSelected);
    }

    [Fact] // C5.9: Multiple Ctrl+Down moves focus only — selection unchanged
    public void C5_9_Multiple_CtrlDown_FocusOnly()
    {
        var (host, lb) = Show(SelectionMode.Multiple);
        using var _ = host;
        FocusItem(host, lb, 0);
        host.SendKey(Key.Space); // select 0
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow, KeyModifiers.Control);
        host.RunUntilIdle();
        Assert.True(Item(lb, 0).IsSelected);  // selection unchanged
        Assert.False(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 1).IsFocused);   // …but the cursor moved
    }

    [Fact] // C5.10: Multiple Shift+Down extends the range from the anchor
    public void C5_10_Multiple_ShiftDown_ExtendsRange()
    {
        var (host, lb) = Show(SelectionMode.Multiple);
        using var _ = host;
        FocusItem(host, lb, 1);
        host.SendKey(Key.Space); // select 1 → anchor 1
        host.RunUntilIdle();

        host.SendKey(Key.DownArrow, KeyModifiers.Shift);
        host.RunUntilIdle();
        Assert.True(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 2).IsSelected);
    }

    [Fact] // C5.11: Space selects the current (single)
    public void C5_11_Single_Space_Selects()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 1);

        host.SendKey(Key.Space);
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);
    }

    [Fact] // C5.12: Space toggles the current (multiple)
    public void C5_12_Multiple_Space_Toggles()
    {
        var (host, lb) = Show(SelectionMode.Multiple);
        using var _ = host;
        FocusItem(host, lb, 1);

        host.SendKey(Key.Space);
        host.RunUntilIdle();
        Assert.True(Item(lb, 1).IsSelected);

        host.SendKey(Key.Space); // toggle off
        host.RunUntilIdle();
        Assert.False(Item(lb, 1).IsSelected);
    }

    [Fact] // C5.13: Enter activates the current item
    public void C5_13_Enter_Activates()
    {
        var (host, lb) = Show();
        using var _ = host;
        FocusItem(host, lb, 1);
        ItemActivatedEventArgs? activated = null;
        lb.ItemActivated += (_, e) => activated = e;

        host.SendKey(Key.Enter);
        host.RunUntilIdle();
        Assert.NotNull(activated);
        Assert.Equal(1, activated!.Index);
    }

    [Fact] // C5.14: the keyboard focus-row renders :focus-visible reverse-video, distinct from a mouse-selected row
    public void C5_14_FocusVisibleCue_DistinctFromMouseSelection()
    {
        var (host, lb) = Show();
        using var _ = host;

        // Keyboard-navigate to item 1 (Directional focus ⇒ :focus-visible reverse-video on row 1).
        FocusItem(host, lb, 0);
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();
        var keyboardRowBg = host.FrameBuffer[1, 1].Style.Background; // [col 1, row 1] inside item 1

        // Mouse-click item 0 (Pointer modality ⇒ :selected fill, NOT :focus-visible).
        host.SendClick(2, 0);
        host.RunUntilIdle();
        var mouseRowBg = host.FrameBuffer[1, 0].Style.Background; // [col 1, row 0] inside item 0

        Assert.NotEqual(keyboardRowBg, mouseRowBg); // reverse-video (TextBrush) ≠ selection fill (SelectionBrush)
    }

    [Fact] // C5.15: removing a non-focused item BEFORE the cursor — the next Down lands on the contiguous item (no skip)
    public void C5_15_RemoveBeforeCursor_NoSkip()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 10) });
        using var _ = host;
        var lb = new ListBox { ItemsSource = source };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        Item(lb, 1).Focus(); // focus "b"
        host.RunUntilIdle();

        source.RemoveAt(0); // "a" removed; "b" is now at index 0 and still focused
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // resolves the cursor from the live focused "b" (idx 0) ⇒ Down → "c"
        host.RunUntilIdle();
        Assert.Equal("c", lb.SelectedItem); // not "d" — the stale-cursor skip is gone
    }

    [Fact] // C5.16: inserting before the cursor — the next Down still lands on the contiguous item
    public void C5_16_InsertBeforeCursor_NoStall()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 10) });
        using var _ = host;
        var lb = new ListBox { ItemsSource = source };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        Item(lb, 1).Focus(); // focus "b"
        host.RunUntilIdle();

        source.Insert(0, "z"); // "b" is now at index 2, still focused
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow); // resolves from the live focused "b" (idx 2) ⇒ Down → "c"
        host.RunUntilIdle();
        Assert.Equal("c", lb.SelectedItem); // not "b" — Down actually advances
    }
}
