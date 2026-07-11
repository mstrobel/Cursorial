using System.Collections.ObjectModel;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Input;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C5 — ListBox keyboard navigation (P9.3b: arrows/Home/End/Space/Enter + the focus-row cue).
public sealed class Section18_ListBoxKeyboard
{
    private static (UIHeadlessHost Host, ListBox List) Show(SelectionMode mode = SelectionMode.Single)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 10) });
        var lb = new ListBox { SelectionMode = mode, ItemsSource = new[] { "a", "b", "c", "d" } };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb);
    }

    private static ListBoxItem Item(ListBox lb, int index) => (ListBoxItem)lb.ItemContainerGenerator.ContainerFromIndex(index)!;

    private static void FocusItem(UIHeadlessHost host, ListBox lb, int index)
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
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 10) });
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
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 10) });
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

    // ── overflow keyboard routing (the Control.HandlesScrolling fix) ──────────────────────────────────
    //
    // When the list overflows its viewport, the inner ScrollViewer must NOT consume the arrow / Home / End
    // keys: a Selector overrides Control.HandlesScrolling, so the ScrollViewer (which sits BELOW the ListBox
    // in the bubble route) leaves the keys unhandled. The ListBox moves the SELECTION, and a GotFocus →
    // EnsureVisible brings the focused item into view (focus-follows-scroll). Before the fix the ScrollViewer
    // ate the keys and scrolled the extent to the bottom before the selection ever moved (the reported bug).
    private static (UIHeadlessHost Host, ListBox List, ScrollViewer Scroll) ShowOverflow(int count = 50, int rows = 10)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, rows) });
        var lb = new ListBox { ItemsSource = Enumerable.Range(0, count).Select(i => $"item{i:000}").ToArray() };
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb, FindDescendant<ScrollViewer>(lb)!);
    }

    private static T? FindDescendant<T>(UIElement root) where T : UIElement
    {
        if (root is T match)
            return match;
        if (root.VisualChildrenList is { } children)
            foreach (var child in children)
                if (FindDescendant<T>(child) is { } found)
                    return found;
        return null;
    }

    // Whether a container is rendered inside the ScrollViewer's viewport (window-coordinate overlap).
    private static bool IsWithinViewport(ScrollViewer scroll, UIElement container)
    {
        var viewportTop = scroll.TranslateToWindow(0, 0).Row;
        var itemTop = container.TranslateToWindow(0, 0).Row;
        return itemTop >= viewportTop && itemTop < viewportTop + scroll.Viewport.Rows;
    }

    [Fact] // C5.17: an overflowing ListBox — Down moves the SELECTION (not the ScrollViewer extent); no premature scroll while the item is visible
    public void C5_17_Overflow_Down_MovesSelection_NotExtent()
    {
        var (host, lb, scroll) = ShowOverflow();
        using var _ = host;
        Assert.True(scroll.Extent.Rows > scroll.Viewport.Rows); // genuinely overflowing

        Item(lb, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.DownArrow);
        host.RunUntilIdle();

        Assert.Equal(1, lb.SelectedIndex);       // selection moved — the ScrollViewer no longer eats the arrow
        Assert.True(Item(lb, 1).IsFocused);
        Assert.Equal(0, scroll.VerticalOffset);  // item 1 is still on-screen → no premature scroll-to-bottom
    }

    [Fact] // C5.18: End on an overflowing ListBox selects the last item AND scrolls it into view (focus-follows-scroll)
    public void C5_18_Overflow_End_BringsLastIntoView()
    {
        var (host, lb, scroll) = ShowOverflow();
        using var _ = host;
        var last = lb.ItemContainerGenerator.ContainerCount - 1;

        Item(lb, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.End);
        host.RunUntilIdle();

        Assert.Equal(last, lb.SelectedIndex);
        var max = Math.Max(0, scroll.Extent.Rows - scroll.Viewport.Rows);
        Assert.Equal(max, scroll.VerticalOffset);             // scrolled to the bottom to reveal the last item
        Assert.True(IsWithinViewport(scroll, Item(lb, last))); // …and it is actually on-screen
    }

    [Fact] // C5.19: arrowing past the viewport bottom scrolls minimally so the focused item stays visible
    public void C5_19_Overflow_DownPastViewport_ScrollsToFollow()
    {
        var (host, lb, scroll) = ShowOverflow();
        using var _ = host;
        var steps = scroll.Viewport.Rows + 3;

        Item(lb, 0).Focus();
        host.RunUntilIdle();
        for (var i = 0; i < steps; i++)
        {
            host.SendKey(Key.DownArrow);
            host.RunUntilIdle();
        }

        Assert.Equal(steps, lb.SelectedIndex);
        Assert.True(scroll.VerticalOffset > 0);                 // the view followed the selection down
        Assert.True(IsWithinViewport(scroll, Item(lb, steps))); // …keeping the focused item visible
    }

    [Fact] // C5.20: Up after scrolling down brings the focused item back up into view (minimal upward scroll)
    public void C5_20_Overflow_UpFromBottom_BringsIntoView()
    {
        var (host, lb, scroll) = ShowOverflow();
        using var _ = host;

        Item(lb, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.End); // jump to the bottom
        host.RunUntilIdle();
        Assert.True(scroll.VerticalOffset > 0);

        host.SendKey(Key.Home); // back to the top
        host.RunUntilIdle();
        Assert.Equal(0, lb.SelectedIndex);
        Assert.Equal(0, scroll.VerticalOffset); // scrolled back up to reveal item 0
    }

    [Fact] // C5.21: PageDown/PageUp move the selection by a viewport page and keep the focused item visible
    public void C5_21_Overflow_PageDown_PageUp_MoveByPage()
    {
        var (host, lb, scroll) = ShowOverflow(count: 50);
        using var _ = host;
        var page = lb.ItemsPerPage();
        Assert.True(page >= 2); // a 10-row viewport pages by multiple items (not a single step)

        Item(lb, 0).Focus();
        host.RunUntilIdle();
        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(page, lb.SelectedIndex);                                   // one page down from item 0
        Assert.True(IsWithinViewport(scroll, Item(lb, lb.SelectedIndex)));      // …kept on screen

        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(Math.Min(49, 2 * page), lb.SelectedIndex);                 // a second page down

        host.SendKey(Key.PageUp);
        host.RunUntilIdle();
        Assert.Equal(Math.Max(0, Math.Min(49, 2 * page) - page), lb.SelectedIndex); // a page back up
        Assert.True(IsWithinViewport(scroll, Item(lb, lb.SelectedIndex)));
    }

    // ───────────────────────────── items host as a focus scope (P1 / ND33) ─────────────────────────────

    // A focusable sibling above the list, so Tab can cross INTO the list from outside it.
    private static (UIHeadlessHost Host, Button Outer, ListBox List) ShowWithOuter(SelectionMode mode = SelectionMode.Single)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 12) });
        var outer = new Button { Content = "Outer" };
        var lb = new ListBox { SelectionMode = mode, ItemsSource = new[] { "a", "b", "c", "d" } };
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(outer);
        root.Children.Add(lb);
        host.ShowRoot(root);
        host.RunUntilIdle();
        return (host, outer, lb);
    }

    [Fact] // C5.17: the items host remembers the focused item — Tab back in lands there, not item 0
    public void C5_17_TabIntoList_LandsOnRememberedItem()
    {
        var (host, outer, lb) = ShowWithOuter();
        using var _ = host;
        Item(lb, 2).Focus();   // focus item 2 → recorded on the items-host focus scope
        host.RunUntilIdle();
        outer.Focus();         // leave the list
        host.RunUntilIdle();
        Assert.True(outer.IsFocused);

        host.SendKey(Key.Tab); // Tab into the list
        host.RunUntilIdle();
        Assert.True(Item(lb, 2).IsFocused);
    }

    [Fact] // C5.18: a never-focused list with no selection enters at item 0; Tab-in does not select
    public void C5_18_TabIntoFreshList_LandsOnFirst_NoSelect()
    {
        var (host, outer, lb) = ShowWithOuter();
        using var _ = host;
        outer.Focus();
        host.RunUntilIdle();
        Assert.Equal(-1, lb.SelectedIndex);

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.True(Item(lb, 0).IsFocused);
        Assert.Equal(-1, lb.SelectedIndex); // a plain focus move — selection-follows-focus is arrow-only
    }

    [Fact] // C5.19: a purely programmatic selection primes the items-host memory → Tab-in lands on it
    public void C5_19_ProgrammaticSelection_PrimesTabInLanding()
    {
        var (host, outer, lb) = ShowWithOuter();
        using var _ = host;
        outer.Focus();        // the list never holds focus
        host.RunUntilIdle();
        lb.SelectedIndex = 2; // programmatic — primes the items-host scope memory
        host.RunUntilIdle();
        Assert.True(outer.IsFocused); // priming does not move focus

        host.SendKey(Key.Tab);
        host.RunUntilIdle();
        Assert.True(Item(lb, 2).IsFocused);
    }

    [Fact] // C5.20: the items host is a focus scope (the structural guarantee behind C5.17–C5.19)
    public void C5_20_ItemsHostIsAFocusScope()
    {
        var (host, lb) = Show();
        using var _ = host;

        var scope = FocusManager.GetFocusScope(Item(lb, 0));
        Assert.NotNull(scope);
        Assert.True(FocusManager.GetIsFocusScope(scope!));
        Assert.IsType<VirtualizingStackPanel>(scope);
    }
}
