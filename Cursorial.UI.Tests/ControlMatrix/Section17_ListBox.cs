using System.Collections.ObjectModel;

using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C4 — ListBox / Selector / ListBoxItem (P9.3a: selection core — mouse + model wiring + theme).
public sealed class Section17_ListBox
{
    private static (UIHeadlessHost Host, ListBox List) Show(ListBox list)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, 10) });
        host.ShowRoot(list);
        host.RunUntilIdle();
        return (host, list);
    }

    private static ItemContainerGenerator Gen(ListBox lb) => lb.ItemContainerGenerator;
    private static ListBoxItem Item(ListBox lb, int index) => (ListBoxItem)Gen(lb).ContainerFromIndex(index)!;

    // Drive a selection gesture the way a click does (the real OnMouseDown path runs this), with modifiers.
    private static void Click(ListBox lb, int index, KeyModifiers modifiers = KeyModifiers.None, int clickCount = 1)
        => lb.HandleContainerPointerSelect(Item(lb, index), modifiers, clickCount);

    [Fact] // C4.1: ListBox realizes ListBoxItem containers
    public void C4_1_RealizesListBoxItems()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;
        Assert.IsType<ListBoxItem>(Gen(lb).ContainerFromIndex(0));
    }

    [Fact] // C4.2: a ListBoxItem item is its own container
    public void C4_2_OwnContainer()
    {
        var leaf = new ListBoxItem { Content = "x" };
        var (host, lb) = Show(new ListBox { ItemsSource = new object[] { leaf } });
        using var _ = host;
        Assert.Same(leaf, Gen(lb).ContainerFromIndex(0));
    }

    [Fact] // C4.3: a plain click selects (SelectedIndex/Item + container IsSelected + :selected)
    public void C4_3_ClickSelects()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        host.SendClick(2, 1); // item index 1 (row 1)
        host.RunUntilIdle();

        Assert.Equal(1, lb.SelectedIndex);
        Assert.Equal("b", lb.SelectedItem);
        Assert.True(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 1).HasCustomPseudoClass(":selected"));
    }

    [Fact] // C4.4: a second click replaces the selection (single mode)
    public void C4_4_ClickReplaces()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        Click(lb, 1);
        Click(lb, 2);
        host.RunUntilIdle();

        Assert.Equal(2, lb.SelectedIndex);
        Assert.False(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 2).IsSelected);
    }

    [Fact] // C4.5: Ctrl-click accumulates (multiple)
    public void C4_5_CtrlClickAccumulates()
    {
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        Click(lb, 0, KeyModifiers.Control);
        Click(lb, 2, KeyModifiers.Control);
        host.RunUntilIdle();

        Assert.True(Item(lb, 0).IsSelected);
        Assert.False(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 2).IsSelected);
        Assert.Equal(2, lb.SelectedItems.Count);
    }

    [Fact] // C4.6: Shift-click selects the range from the anchor (multiple)
    public void C4_6_ShiftClickRange()
    {
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = new[] { "a", "b", "c", "d" } });
        using var _ = host;

        Click(lb, 1);                          // anchor
        Click(lb, 3, KeyModifiers.Shift);       // range 1..3
        host.RunUntilIdle();

        Assert.True(Item(lb, 1).IsSelected);
        Assert.True(Item(lb, 2).IsSelected);
        Assert.True(Item(lb, 3).IsSelected);
        Assert.False(Item(lb, 0).IsSelected);
    }

    [Fact] // C4.7: setting SelectedIndex flips the container's IsSelected + :selected
    public void C4_7_SelectedIndexSetter()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        lb.SelectedIndex = 2;
        host.RunUntilIdle();
        Assert.True(Item(lb, 2).IsSelected);
        Assert.True(Item(lb, 2).HasCustomPseudoClass(":selected"));
    }

    [Fact] // C4.8: setting SelectedItem selects by item
    public void C4_8_SelectedItemSetter()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        lb.SelectedItem = "b";
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);
        Assert.True(Item(lb, 1).IsSelected);
    }

    [Fact] // C4.9: a click updates the SelectedIndex/SelectedItem DirectProperties (the binding surface)
    public void C4_9_ClickUpdatesDirectProperties()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        host.SendClick(2, 2);
        host.RunUntilIdle();
        Assert.Equal(2, lb.GetValue(SelectingItemsControl.SelectedIndexProperty));
        Assert.Equal("c", lb.GetValue(SelectingItemsControl.SelectedItemProperty));
    }

    [Fact] // C4.10: setting ListBoxItem.IsSelected directly folds into the model
    public void C4_10_ContainerIsSelectedFoldsIn()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        Item(lb, 1).IsSelected = true;
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);
    }

    [Fact] // C4.11: deselecting (selecting another) clears the old container's :selected
    public void C4_11_OldSelectionClears()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        Click(lb, 1);
        host.RunUntilIdle();
        Click(lb, 2);
        host.RunUntilIdle();
        Assert.False(Item(lb, 1).IsSelected);
        Assert.False(Item(lb, 1).HasCustomPseudoClass(":selected"));
    }

    [Fact] // C4.12: removing the selected item re-targets to the nearest survivor (ListBox policy CD-P9-9)
    public void C4_12_RemoveSelected_ReTargets()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, lb) = Show(new ListBox { ItemsSource = source });
        using var _ = host;
        Click(lb, 1);
        host.RunUntilIdle();

        source.RemoveAt(1); // remove the selected "b"
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);     // re-targeted to the item now at index 1 ("c")
        Assert.True(Item(lb, 1).IsSelected);
    }

    [Fact] // C4.13: removing the selected LAST item re-targets to the new last
    public void C4_13_RemoveLastSelected_ReTargets()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, lb) = Show(new ListBox { ItemsSource = source });
        using var _ = host;
        Click(lb, 2);
        host.RunUntilIdle();

        source.RemoveAt(2);
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex); // new last
    }

    [Fact] // C4.14: a double-click raises ItemActivated with the item + index
    public void C4_14_DoubleClickActivates()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;
        ItemActivatedEventArgs? activated = null;
        lb.ItemActivated += (_, e) => activated = e;

        Click(lb, 2, clickCount: 2);
        host.RunUntilIdle();

        Assert.NotNull(activated);
        Assert.Equal(2, activated!.Index);
        Assert.Equal("c", activated.Item);
    }

    [Fact] // C4.15: SelectionChanged fires on a selection change
    public void C4_15_SelectionChangedFires()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;
        var fired = 0;
        lb.SelectionChanged += (_, _) => fired++;

        Click(lb, 1);
        host.RunUntilIdle();
        Assert.Equal(1, fired);
    }

    [Fact] // C4.16: SelectionMode Multiple→Single collapses to the lead
    public void C4_16_ModeNarrowCollapses()
    {
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;
        Click(lb, 0, KeyModifiers.Control);
        Click(lb, 1, KeyModifiers.Control);
        Click(lb, 2, KeyModifiers.Control); // lead = 2

        lb.SelectionMode = SelectionMode.Single;
        host.RunUntilIdle();
        Assert.Single(lb.SelectedItems);
        Assert.Equal(2, lb.SelectedIndex);
        Assert.False(Item(lb, 0).IsSelected);
    }

    [Fact] // C4.17: inserting before the selection shifts SelectedIndex with the item
    public void C4_17_InsertShiftsSelection()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, lb) = Show(new ListBox { ItemsSource = source });
        using var _ = host;
        Click(lb, 2);
        host.RunUntilIdle();

        source.Insert(0, "z");
        host.RunUntilIdle();
        Assert.Equal(3, lb.SelectedIndex);     // followed the item ("c") to index 3
        Assert.True(Item(lb, 3).IsSelected);
    }

    [Fact] // C4.18: selecting a row visibly changes its rendering (the selection bar — :selected restyle paints)
    public void C4_18_SelectingRowChangesItsRendering()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        var before = host.FrameBuffer[1, 0].Style; // [column 1, row 0] — the 'a' glyph cell (Border padding insets content to col 1)
        host.SendClick(2, 0);                       // select item 0
        host.RunUntilIdle();

        Assert.NotEqual(before, host.FrameBuffer[1, 0].Style); // :selected restyle changed the rendered cell (the SelectionBrush fill)
        Assert.True(Item(lb, 0).IsSelected);
    }

    [Fact] // C4.19: the list is not a tab stop; its items are focusable
    public void C4_19_TabStopAndFocusable()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b" } });
        using var _ = host;
        Assert.False(lb.IsTabStop);
        Assert.True(Item(lb, 0).Focusable);
    }

    [Fact] // C4.20: a collection Move carries the selection with the moved items (multiple)
    public void C4_20_MoveCarriesSelection()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = source });
        using var _ = host;
        Click(lb, 0, KeyModifiers.Control);
        Click(lb, 2, KeyModifiers.Control); // select a (0) + c (2)

        source.Move(0, 2); // → [b, c, a]; a now at 2, c at 1
        host.RunUntilIdle();
        Assert.False(Item(lb, 0).IsSelected); // b
        Assert.True(Item(lb, 1).IsSelected);  // c
        Assert.True(Item(lb, 2).IsSelected);  // a
    }

    [Fact] // C4.21: removing an UNSELECTED item before the lead keeps SelectedItem aligned with SelectedIndex
    public void C4_21_RemoveBeforeLead_SelectedItemStaysAligned()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, lb) = Show(new ListBox { ItemsSource = source });
        using var _ = host;
        Click(lb, 2); // select "c"
        host.RunUntilIdle();

        source.RemoveAt(0); // remove the unselected "a" before the lead
        host.RunUntilIdle();
        Assert.Equal(1, lb.SelectedIndex);  // "c" is now at index 1
        Assert.Equal("c", lb.SelectedItem); // …and SelectedItem still resolves to "c" (not the stale "b")
    }

    [Fact] // C4.22: an own-container constructed pre-selected folds into the model on realization
    public void C4_22_PresetOwnContainer_FoldsIn()
    {
        var leaf = new ListBoxItem { Content = "x", IsSelected = true };
        var (host, lb) = Show(new ListBox { ItemsSource = new object[] { leaf, "y" } });
        using var _ = host;

        Assert.Equal(0, lb.SelectedIndex);   // folded into the model
        Assert.Same(leaf, lb.SelectedItem);

        Click(lb, 1); // single mode — selecting another clears the leaf
        host.RunUntilIdle();
        Assert.False(leaf.IsSelected);
    }

    [Fact] // C4.23: an out-of-range SelectedIndex clamps to "no selection" (SelectedIndex/SelectedItem stay consistent)
    public void C4_23_OutOfRangeSelectedIndex_Clamps()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        lb.SelectedIndex = 99;
        host.RunUntilIdle();
        Assert.Equal(-1, lb.SelectedIndex);
        Assert.Null(lb.SelectedItem);
    }

    [Fact] // C4.24: removing a NON-selected item shifts the selection without an event; survivors stay selected
    public void C4_24_RemoveNonSelected_Multiple_NoEvent()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = source });
        using var _ = host;
        Click(lb, 1, KeyModifiers.Control);
        Click(lb, 3, KeyModifiers.Control); // select {1,3} = b,d
        host.RunUntilIdle();
        var events = 0;
        lb.SelectionChanged += (_, _) => events++;

        source.RemoveAt(0); // remove unselected "a"; {1,3} → {0,2}
        host.RunUntilIdle();
        Assert.True(Item(lb, 0).IsSelected);  // b shifted to 0, still selected
        Assert.False(Item(lb, 1).IsSelected); // c
        Assert.True(Item(lb, 2).IsSelected);  // d shifted to 2, still selected
        Assert.Equal(0, events);              // pure shift — no membership change
    }

    [Fact] // C4.25: removing ONE of several selected items drops just it + fires once
    public void C4_25_RemoveOneOfMultiSelected_FiresOnce()
    {
        var source = new ObservableCollection<string> { "a", "b", "c", "d" };
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = source });
        using var _ = host;
        Click(lb, 1, KeyModifiers.Control);
        Click(lb, 3, KeyModifiers.Control); // {1,3} = b,d
        host.RunUntilIdle();
        var events = 0;
        lb.SelectionChanged += (_, _) => events++;

        source.RemoveAt(1); // remove the selected "b"; d (3) → 2 survives selected
        host.RunUntilIdle();
        Assert.Equal(1, events);
        Assert.True(Item(lb, 2).IsSelected); // d survives at 2, still selected
        Assert.Single(lb.SelectedItems);
    }

    [Fact] // C4.27: a long list realizes all items (eager) AND scrolls — the ItemsPresenter hosts through the ScrollViewer
    public void C4_27_LongList_ScrollsThroughViewer()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = Enumerable.Range(0, 20).Select(i => $"item{i}").ToArray() });
        using var _ = host;

        Assert.Equal(20, Gen(lb).ContainerCount); // eager: every item realized
        var scroll = FindDescendant<ScrollViewer>(lb);
        Assert.NotNull(scroll); // the ListBox template hosts the items in a ScrollViewer

        var rowBefore = Item(lb, 0).TranslateToWindow(0, 0).Row;
        scroll.VerticalOffset = 5;
        host.RunUntilIdle();
        var rowAfter = Item(lb, 0).TranslateToWindow(0, 0).Row;
        Assert.True(rowAfter < rowBefore); // scrolling down moved item 0 up (out of the viewport) — the SCP slid the band
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

    [Fact] // C4.26: swapping ItemsSource clears the selection — never spuriously selects index 0 (re-source/reset)
    public void C4_26_ReSource_ClearsSelection()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b" } });
        using var _ = host;
        Click(lb, 0);
        host.RunUntilIdle();
        Assert.Equal(0, lb.SelectedIndex);

        lb.ItemsSource = new[] { "x", "y", "z" };
        host.RunUntilIdle();
        Assert.Equal(-1, lb.SelectedIndex); // cleared, NOT spuriously re-targeted to 0
    }
    
    [Fact] // C4.x: a Ctrl+click that DESELECTS clears that container without desyncing from the model
    //        (the ApplySelection toggle-off path — the bug forced the container back to :selected).
    public void C4_CtrlClickDeselect_Multiple_ClearsContainer()
    {
        var (host, lb) = Show(new ListBox { SelectionMode = SelectionMode.Multiple, ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        Click(lb, 0, KeyModifiers.Control);   // select a
        Click(lb, 1, KeyModifiers.Control);   // + select b  → {0,1}
        host.RunUntilIdle();
        Assert.True(Item(lb, 0).IsSelected);
        Assert.True(Item(lb, 1).IsSelected);

        Click(lb, 1, KeyModifiers.Control);   // Ctrl+click b again → toggle OFF
        host.RunUntilIdle();

        Assert.True(Item(lb, 0).IsSelected);                          // a stays selected
        Assert.False(Item(lb, 1).IsSelected);                         // b's container cleared (was forced back to true)
        Assert.False(Item(lb, 1).HasCustomPseudoClass(":selected")); // …and :selected is off
    }

    [Fact] // C4.23b: setting SelectedIndex out-of-range from a LIVE selection clamps to none
    //        (C4.23 only ever clamped from the default -1, so it never exercised clear-from-a-selection).
    public void C4_23b_OutOfRangeSelectedIndex_ClearsLiveSelection()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        lb.SelectedIndex = 1;                 // a live selection
        host.RunUntilIdle();
        Assert.Equal("b", lb.SelectedItem);

        lb.SelectedIndex = 99;                // out of range → clamp to no selection
        host.RunUntilIdle();
        Assert.Equal(-1, lb.SelectedIndex);
        Assert.Null(lb.SelectedItem);
        Assert.False(Item(lb, 1).IsSelected); // the previously-selected container cleared too
    }

    [Fact] // C4.23c: SelectedItem set to a non-member from a live selection also clears (two-way-binding safety).
    public void C4_23c_NonMemberSelectedItem_ClearsLiveSelection()
    {
        var (host, lb) = Show(new ListBox { ItemsSource = new[] { "a", "b", "c" } });
        using var _ = host;

        lb.SelectedIndex = 1;
        host.RunUntilIdle();

        lb.SelectedItem = "zzz";              // not in Items
        host.RunUntilIdle();
        Assert.Equal(-1, lb.SelectedIndex);
        Assert.Null(lb.SelectedItem);
    }
}
