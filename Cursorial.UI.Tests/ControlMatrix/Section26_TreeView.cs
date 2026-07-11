using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C13 — TreeView / TreeViewItem (P2C): a hierarchical selector with tree-wide single selection.
// TreeViewItem is both a row (Header) and a sub-items host (Items); IsExpanded gates the children + the twisty;
// selection is coordinated tree-wide by the owning TreeView. Arrows navigate the VISIBLE tree (collapsed subtrees
// skipped) and selection follows the keyboard cursor.
public sealed class Section26_TreeView
{
    // a (expanded?) ─ a1 (expanded?) ─ a1x ; a ─ a2 ; b   — built from own-container TreeViewItems.
    private sealed record Tree(UIHeadlessHost Host, TreeView View, TreeViewItem A, TreeViewItem A1, TreeViewItem A1x, TreeViewItem A2, TreeViewItem B);

    private static Tree Show(bool expandA = false, bool expandA1 = false)
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 14) });

        var a1x = new TreeViewItem { Header = "A.1.x" };
        var a1 = new TreeViewItem { Header = "A.1", IsExpanded = expandA1 };
        a1.Items.Add(a1x);
        var a2 = new TreeViewItem { Header = "A.2" };
        var a = new TreeViewItem { Header = "A", IsExpanded = expandA };
        a.Items.Add(a1);
        a.Items.Add(a2);
        var b = new TreeViewItem { Header = "B" };

        var view = new TreeView
        {
            Width = 30, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        view.Items.Add(a);
        view.Items.Add(b);

        host.ShowRoot(view);
        host.RunUntilIdle();
        return new Tree(host, view, a, a1, a1x, a2, b);
    }

    [Fact] // regression: focusing an expanded node whose subtree overflows the viewport keeps the header visible
    public void ExpandedNodeTallerThanViewport_WhenFocused_KeepsHeaderVisible()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 14) });

        var view = new TreeView
        {
            Width = 30, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // A few leaf siblings ABOVE the node, so the node's header sits below the viewport top (rectStart >
        // viewStart) — the real scenario. With the header at content-row 0 the bug can't manifest (no scroll
        // is needed either way), so the regression must place it mid-viewport.
        for (var i = 0; i < 3; i++)
            view.Items.Add(new TreeViewItem { Header = $"sib-{i}" });

        var parent = new TreeViewItem { Header = "PARENT", IsExpanded = true };
        for (var i = 0; i < 30; i++)
            parent.Items.Add(new TreeViewItem { Header = $"child-{i:00}" });
        view.Items.Add(parent);

        host.ShowRoot(view);
        host.RunUntilIdle();

        // Focusing the expanded parent fires BringIntoView-on-focus with the node's full bounds — header + the
        // 30-child subtree, far taller than the 14-row viewport. The header must stay visible, NOT be scrolled
        // off the top to reveal the subtree's bottom (the reported collapse-bring-into-view bug).
        parent.Focus();
        host.RunUntilIdle();

        var headerVisible = Enumerable.Range(0, 14).Any(r => host.GetRowText(r).Contains("PARENT"));
        Assert.True(headerVisible, "the expanded node's header was scrolled out of the viewport");
    }

    [Fact] // C13.1: own-container TreeViewItems realize; parents report HasItems, leaves do not
    public void C13_1_RealizeAndHasItems()
    {
        var t = Show();
        using var _ = t.Host;

        Assert.Same(t.A, t.View.ItemContainerGenerator.ContainerFromIndex(0)); // own-container (itself)
        Assert.Same(t.B, t.View.ItemContainerGenerator.ContainerFromIndex(1));
        Assert.True(t.A.HasItems);    // A has A.1 + A.2
        Assert.True(t.A1.HasItems);   // A.1 has A.1.x
        Assert.False(t.A1x.HasItems); // a leaf
        Assert.False(t.B.HasItems);   // a leaf
    }

    [Fact] // C13.2: IsExpanded gates the children's layout + flips :expanded + raises Expanded/Collapsed
    public void C13_2_ExpandCollapse()
    {
        var t = Show();
        using var _ = t.Host;

        var expanded = 0;
        var collapsed = 0;
        t.A.Expanded += (_, _) => expanded++;
        t.A.Collapsed += (_, _) => collapsed++;

        Assert.True(t.A1.Bounds.IsEmpty);            // collapsed parent ⇒ child never arranged (no layout space)
        Assert.Equal(Visibility.Collapsed, t.A.ItemsHost!.Visibility);
        Assert.False(t.A.HasCustomPseudoClass(":expanded"));

        t.A.IsExpanded = true;
        t.Host.RunUntilIdle();
        Assert.True(t.A.HasCustomPseudoClass(":expanded"));
        Assert.Equal(Visibility.Visible, t.A.ItemsHost!.Visibility);
        Assert.False(t.A1.Bounds.IsEmpty);           // now laid out
        Assert.Equal(1, expanded);

        t.A.IsExpanded = false;
        t.Host.RunUntilIdle();
        Assert.False(t.A.HasCustomPseudoClass(":expanded"));
        Assert.Equal(Visibility.Collapsed, t.A.ItemsHost!.Visibility); // children hidden again (the gate IsExpanded drives)
        Assert.Equal(1, collapsed);
    }

    [Fact] // C13.3: clicking a node's header selects it tree-wide (single); selecting another clears the first
    public void C13_3_ClickSelectsTreeWideSingle()
    {
        var t = Show();
        using var _ = t.Host;

        TreeViewSelectionChangedEventArgs? last = null;
        t.View.SelectionChanged += (_, e) => last = e;

        ClickHeader(t.Host, t.A);
        Assert.True(t.A.IsSelected);
        Assert.Same(t.A, t.View.SelectedItem); // own-container resolves to itself
        Assert.NotNull(last);
        Assert.Same(t.A, last!.NewItem);

        ClickHeader(t.Host, t.B);
        Assert.True(t.B.IsSelected);
        Assert.False(t.A.IsSelected); // tree-wide single: A cleared
        Assert.Same(t.B, t.View.SelectedItem);
    }

    [Fact] // C13.4: clicking the twisty toggles expansion only — it does not move the selection
    public void C13_4_TwistyTogglesWithoutSelecting()
    {
        var t = Show();
        using var _ = t.Host;

        ClickHeader(t.Host, t.B); // selection lives on B
        Assert.True(t.B.IsSelected);

        ClickTwisty(t.Host, t.A); // click A's '>' glyph
        t.Host.RunUntilIdle();
        Assert.True(t.A.IsExpanded);  // A toggled open
        Assert.False(t.A.IsSelected); // selection did NOT move to A
        Assert.True(t.B.IsSelected);
    }

    [Fact] // C13.5: keyboard Right expands in place; Right again descends to the first child (selecting it)
    public void C13_5_RightExpandsThenDescends()
    {
        var t = Show();
        using var _ = t.Host;
        t.A.Focus();
        t.Host.RunUntilIdle();

        t.Host.SendKey(Key.RightArrow); // expand in place
        t.Host.RunUntilIdle();
        Assert.True(t.A.IsExpanded);
        Assert.False(t.A1.IsSelected); // focus stayed on A

        t.Host.SendKey(Key.RightArrow); // descend to A.1
        t.Host.RunUntilIdle();
        Assert.Same(t.A1, t.View.SelectedContainer);
        Assert.True(t.A1.IsSelected);
    }

    [Fact] // C13.6: keyboard Left collapses an expanded node, then ascends to the parent
    public void C13_6_LeftCollapsesThenAscends()
    {
        var t = Show(expandA: true, expandA1: true);
        using var _ = t.Host;
        t.A1.Focus(); // an expanded child node
        t.Host.RunUntilIdle();

        t.Host.SendKey(Key.LeftArrow); // collapse A.1 in place
        t.Host.RunUntilIdle();
        Assert.False(t.A1.IsExpanded);

        t.Host.SendKey(Key.LeftArrow); // ascend to the parent A
        t.Host.RunUntilIdle();
        Assert.Same(t.A, t.View.SelectedContainer);

        // A leaf's first Left ascends directly to its parent.
        t.A1.IsExpanded = true;
        t.Host.RunUntilIdle();
        t.A1x.Focus();
        t.Host.SendKey(Key.LeftArrow);
        t.Host.RunUntilIdle();
        Assert.Same(t.A1, t.View.SelectedContainer);
    }

    [Fact] // C13.7: Down/Up walk the visible tree in pre-order; selection follows the cursor
    public void C13_7_DownUpWalkVisibleTree()
    {
        var t = Show(expandA: true, expandA1: true);
        using var _ = t.Host;
        t.A.Focus();
        t.Host.RunUntilIdle();

        // Visible order: A, A.1, A.1.x, A.2, B
        foreach (var expected in new[] { t.A1, t.A1x, t.A2, t.B })
        {
            t.Host.SendKey(Key.DownArrow);
            t.Host.RunUntilIdle();
            Assert.Same(expected, t.View.SelectedContainer);
        }

        foreach (var expected in new[] { t.A2, t.A1x, t.A1, t.A })
        {
            t.Host.SendKey(Key.UpArrow);
            t.Host.RunUntilIdle();
            Assert.Same(expected, t.View.SelectedContainer);
        }
    }

    [Fact] // C13.7b: PageDown/PageUp page across the visible tree (clamped at the last/first visible node)
    public void C13_7b_PageDownUp_AcrossVisibleTree()
    {
        var t = Show(expandA: true, expandA1: true); // visible: A, A.1, A.1.x, A.2, B (fits the 14-row viewport)
        using var _ = t.Host;
        t.A.Focus();
        t.Host.RunUntilIdle();

        t.Host.SendKey(Key.PageDown);
        t.Host.RunUntilIdle();
        Assert.Same(t.B, t.View.SelectedContainer); // paged to the last visible node
        Assert.True(t.B.IsFocused);

        t.Host.SendKey(Key.PageUp);
        t.Host.RunUntilIdle();
        Assert.Same(t.A, t.View.SelectedContainer); // …and back to the first
        Assert.True(t.A.IsFocused);
    }

    [Fact] // C13.8: Down from a collapsed parent skips its hidden children → the next sibling
    public void C13_8_DownSkipsCollapsedSubtree()
    {
        var t = Show(); // A collapsed
        using var _ = t.Host;
        t.A.Focus();
        t.Host.RunUntilIdle();

        t.Host.SendKey(Key.DownArrow);
        t.Host.RunUntilIdle();
        Assert.Same(t.B, t.View.SelectedContainer); // skipped A.1 / A.2 (hidden)
    }

    [Fact] // C13.9: nesting indents recursively — a child's window column exceeds its parent's
    public void C13_9_RecursiveIndent()
    {
        var t = Show(expandA: true);
        using var _ = t.Host;

        var aX = t.A.TranslateToWindow(0, 0).Column;
        var a1X = t.A1.TranslateToWindow(0, 0).Column;
        Assert.True(a1X > aX, $"child column {a1X} should exceed parent column {aX}");
    }

    [Fact] // C13.10: removing the selected node clears the tree's selection (no dangling SelectedItem)
    public void C13_10_RemovingSelectedClearsSelection()
    {
        var t = Show(expandA: true);
        using var _ = t.Host;

        t.A1.IsSelected = true; // select a child node (external set folds into the tree)
        t.Host.RunUntilIdle();
        Assert.Same(t.A1, t.View.SelectedContainer);

        t.A.Items.Remove(t.A1); // remove the selected node (it detaches)
        t.Host.RunUntilIdle();
        Assert.Null(t.View.SelectedContainer);
        Assert.Null(t.View.SelectedItem);
    }

    [Fact] // C13.11: IsSelected set BEFORE attach folds into the tree on realization; a later select clears it (single)
    public void C13_11_PresetSelectionFoldsIn()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(30, 14) });
        using var _ = host;

        var a = new TreeViewItem { Header = "A", IsSelected = true }; // selected before being parented under the tree
        var b = new TreeViewItem { Header = "B" };
        var view = new TreeView { Width = 30, Height = 14 };
        view.Items.Add(a);
        view.Items.Add(b);
        host.ShowRoot(view);
        host.RunUntilIdle();

        Assert.Same(a, view.SelectedContainer); // the preset intent folded in (not dropped)
        Assert.Same(a, view.SelectedItem);

        b.IsSelected = true; // a later selection must clear A — tree-wide single
        host.RunUntilIdle();
        Assert.Same(b, view.SelectedContainer);
        Assert.False(a.IsSelected);
    }

    [Fact] // C13.12: Ctrl+Space is NOT a select — it bubbles unhandled; a modifier-free Space selects the focused node
    public void C13_12_CtrlSpaceBubbles()
    {
        var t = Show();
        using var _ = t.Host;
        t.B.Focus();
        t.Host.RunUntilIdle();

        t.Host.SendKey(Key.Space, KeyModifiers.Control); // Ctrl+Space (the lone real Key.Space wire) must not select
        t.Host.RunUntilIdle();
        Assert.Null(t.View.SelectedContainer);

        t.Host.SendKey(Key.Space); // a modifier-free Space selects the focused node
        t.Host.RunUntilIdle();
        Assert.Same(t.B, t.View.SelectedContainer);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    // Click the header text (2 cells in, past the twisty) — selects the node.
    private static void ClickHeader(UIHeadlessHost host, TreeViewItem item)
    {
        var p = item.TranslateToWindow(2, 0);
        host.SendClick(p.Column, p.Row);
        host.RunUntilIdle();
    }

    // Click the twisty glyph (column 0) — toggles expansion.
    private static void ClickTwisty(UIHeadlessHost host, TreeViewItem item)
    {
        var p = item.TranslateToWindow(0, 0);
        host.SendClick(p.Column, p.Row);
        host.RunUntilIdle();
    }
}
