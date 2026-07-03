using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C9 — TabControl / TabItem (P9.5): single-selection over SelectingItemsControl; the strip shows
// the headers, the content host shows the selected tab's content; click + keyboard select; the first tab auto-selects.
public sealed class Section22_TabControl
{
    private static (UITestHost Host, TabControl Tabs, TabItem A, TabItem B, TabItem C) ThreeTabs()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        var a = new TabItem { Header = "A", Content = "bodyA" };
        var b = new TabItem { Header = "B", Content = "bodyB" };
        var c = new TabItem { Header = "C", Content = "bodyC" };
        var tabs = new TabControl();
        tabs.Items.Add(a);
        tabs.Items.Add(b);
        tabs.Items.Add(c);
        host.ShowRoot(tabs);
        host.RunUntilIdle();
        return (host, tabs, a, b, c);
    }

    private static void Click(UITestHost host, UIElement element)
    {
        var origin = element.TranslateToWindow(0, 0);
        host.SendClick(origin.Column + 1, origin.Row);
        host.RunUntilIdle();
    }

    [Fact] // C9.1: containers are TabItems; a TabItem is its own container, a data item is wrapped
    public void C9_1_Containers()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        var own = new TabItem { Header = "A", Content = "bodyA" };
        var tabs = new TabControl();
        tabs.Items.Add(own);
        tabs.Items.Add("plain"); // a data item → wrapped in a TabItem
        host.ShowRoot(tabs);
        host.RunUntilIdle();

        Assert.Same(own, tabs.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.IsType<TabItem>(tabs.ItemContainerGenerator.ContainerFromIndex(1));
    }

    [Fact] // C9.2: the first tab auto-selects + its content shows
    public void C9_2_FirstTabAutoSelects()
    {
        var (host, _, a, _, _) = ThreeTabs();
        using var _ = host;

        Assert.Equal(0, ((TabControl)a.LogicalParent!).SelectedIndex);
        Assert.True(a.IsSelected);
        Assert.True(a.HasCustomPseudoClass(":selected"));
    }

    [Fact] // C9.3: SelectedContent shows the selected tab's content
    public void C9_3_SelectedContentReflectsSelection()
    {
        var (host, tabs, _, _, _) = ThreeTabs();
        using var _ = host;
        Assert.Equal("bodyA", tabs.SelectedContent); // auto-selected tab 0

        tabs.SelectedIndex = 2;
        host.RunUntilIdle();
        Assert.Equal("bodyC", tabs.SelectedContent); // switched
    }

    [Fact] // C9.4: clicking a tab header selects it (and deselects the others — single mode)
    public void C9_4_ClickSelects()
    {
        var (host, tabs, a, b, _) = ThreeTabs();
        using var _ = host;

        Click(host, b);
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(b.IsSelected);
        Assert.True(b.HasCustomPseudoClass(":selected"));
        Assert.False(a.IsSelected); // single selection — A deselected
        Assert.Equal("bodyB", tabs.SelectedContent);
    }

    [Fact] // C9.5: Left/Right arrows move the selection (selection-follows-focus)
    public void C9_5_ArrowsMove()
    {
        var (host, tabs, a, b, _) = ThreeTabs();
        using var _ = host;
        a.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(b.IsSelected);

        host.SendKey(Key.LeftArrow);
        host.RunUntilIdle();
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.True(a.IsSelected);
    }

    [Fact] // C9.6: Home/End jump to the ends
    public void C9_6_HomeEnd()
    {
        var (host, tabs, a, _, c) = ThreeTabs();
        using var _ = host;
        a.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.End);
        host.RunUntilIdle();
        Assert.Equal(2, tabs.SelectedIndex);
        Assert.True(c.IsSelected);

        host.SendKey(Key.Home);
        host.RunUntilIdle();
        Assert.Equal(0, tabs.SelectedIndex);
    }

    [Fact] // C9.7: Ctrl+PageDown/PageUp cycle (wrap around)
    public void C9_7_CtrlPageCycles()
    {
        var (host, tabs, a, _, c) = ThreeTabs();
        using var _ = host;
        a.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.PageUp, KeyModifiers.Control); // wrap backwards from 0 → 2
        host.RunUntilIdle();
        Assert.Equal(2, tabs.SelectedIndex);
        Assert.True(c.IsSelected);

        host.SendKey(Key.PageDown, KeyModifiers.Control); // wrap forwards from 2 → 0
        host.RunUntilIdle();
        Assert.Equal(0, tabs.SelectedIndex);
    }

    [Fact] // C9.8: SelectedItem reflects the selected container
    public void C9_8_SelectedItem()
    {
        var (host, tabs, _, b, _) = ThreeTabs();
        using var _ = host;

        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.Same(b, tabs.SelectedItem);
    }

    [Fact] // C9.9: setting TabItem.IsSelected folds into the single-selection model (selecting one deselects the rest)
    public void C9_9_IsSelectedFoldsIntoModel()
    {
        var (host, tabs, a, _, c) = ThreeTabs();
        using var _ = host;
        Assert.True(a.IsSelected); // auto-selected

        c.IsSelected = true; // set directly on the container
        host.RunUntilIdle();
        Assert.Equal(2, tabs.SelectedIndex);
        Assert.True(c.IsSelected);
        Assert.False(a.IsSelected); // single mode — the old selection cleared
    }

    [Fact] // C9.10: exactly one tab carries :selected at a time
    public void C9_10_OneSelectedAtATime()
    {
        var (host, tabs, a, b, c) = ThreeTabs();
        using var _ = host;

        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.False(a.HasCustomPseudoClass(":selected"));
        Assert.True(b.HasCustomPseudoClass(":selected"));
        Assert.False(c.HasCustomPseudoClass(":selected"));
    }

    [Fact] // C9.11: Ctrl+arrow / Ctrl+Home / Ctrl+End do NOT navigate (the !ctrl guards fall through unhandled)
    public void C9_11_CtrlArrowsDoNotNavigate()
    {
        var (host, tabs, _, b, _) = ThreeTabs();
        using var _ = host;
        tabs.SelectedIndex = 1; // middle, so a wrong move in either direction is observable
        host.RunUntilIdle();
        b.Focus();
        host.RunUntilIdle();

        foreach (var key in (Key[])[Key.LeftArrow, Key.RightArrow, Key.Home, Key.End])
        {
            host.SendKey(key, KeyModifiers.Control);
            host.RunUntilIdle();
            Assert.Equal(1, tabs.SelectedIndex); // unchanged — Ctrl is reserved for PageUp/PageDown
        }
    }

    [Fact] // C9.12: PageUp/PageDown WITHOUT Ctrl do NOT navigate (the cycle is Ctrl-gated)
    public void C9_12_PlainPageKeysDoNotNavigate()
    {
        var (host, tabs, _, b, _) = ThreeTabs();
        using var _ = host;
        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        b.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.PageUp);
        host.RunUntilIdle();
        Assert.Equal(1, tabs.SelectedIndex);
        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [Fact] // C9.13: UIElement (non-string) content switches between tabs without double-hosting (single visual parent)
    public void C9_13_UIElementContentSwitches()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        using var _ = host;
        var bodyA = new Border { Child = new TextBlock { Text = "A-body" } };
        var bodyB = new Border { Child = new TextBlock { Text = "B-body" } };
        var a = new TabItem { Header = "A", Content = bodyA };
        var b = new TabItem { Header = "B", Content = bodyB };
        var tabs = new TabControl();
        tabs.Items.Add(a);
        tabs.Items.Add(b);
        host.ShowRoot(tabs);
        host.RunUntilIdle();

        Assert.Same(bodyA, tabs.SelectedContent); // tab A auto-selected → its UIElement content shows
        Assert.True(bodyA.IsAttachedToTree);       // hosted (in the content host, not the strip)

        tabs.SelectedIndex = 1;                    // switch to B (must not throw a double-visual-parent error)
        host.RunUntilIdle();
        Assert.Same(bodyB, tabs.SelectedContent);

        tabs.SelectedIndex = 0;                    // switch back to A — re-hosts cleanly
        host.RunUntilIdle();
        Assert.Same(bodyA, tabs.SelectedContent);
        Assert.True(bodyA.IsAttachedToTree);
    }

    [Fact] // C9.14: keyboard navigation on an empty TabControl is a safe no-op (the count==0 guard; no DivideByZero on Ctrl+Page)
    public void C9_14_EmptyTabControlKeyboardNoOp()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        using var _ = host;
        var tabs = new TabControl { Focusable = true }; // no items
        host.ShowRoot(tabs);
        host.RunUntilIdle();
        tabs.Focus();
        host.RunUntilIdle();

        host.SendKey(Key.PageDown, KeyModifiers.Control); // % count with count==0 would throw without the guard
        host.RunUntilIdle();
        host.SendKey(Key.RightArrow);
        host.RunUntilIdle();
        Assert.Equal(-1, tabs.SelectedIndex); // nothing selected, no exception
    }

    [Fact] // C9.15: clicking the already-selected tab keeps it selected (idempotent)
    public void C9_15_ClickSelectedTabIdempotent()
    {
        var (host, tabs, _, b, _) = ThreeTabs();
        using var _ = host;
        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.True(b.IsSelected);

        Click(host, b); // click B again
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(b.IsSelected);
    }

    [Fact] // C9.16 (audit / CD-P9-27): a programmatic selection on an UNFOCUSED TabControl must NOT corrupt the
           // enclosing scope's focus memory — the tab strip is not a focus scope (unlike a ListBox items host), so the
           // selection-prime must fence itself off rather than write the strip's enclosing window/surface-root scope
           // (which would steal focus on re-activation). Same guard protects ComboBox.
    public void C9_16_ProgrammaticSelect_DoesNotCorruptEnclosingScopeMemory()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(60, 16) });
        using var _ = host;
        var lead = new Button { Content = "Lead" };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "A", Content = "a" });
        tabs.Items.Add(new TabItem { Header = "B", Content = "b" });
        var root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(lead);
        root.Children.Add(tabs);
        host.ShowRoot(root);
        host.RunUntilIdle();

        var focus = host.Application.FocusManager;
        Assert.True(lead.Focus());                                    // root-scope memory = lead
        host.RunUntilIdle();
        var rootScope = FocusManager.GetFocusScope(lead);
        Assert.Same(lead, FocusManager.GetFocusedElement(rootScope));

        tabs.SelectedIndex = 1;                                       // programmatic; the tab strip is unfocused
        host.RunUntilIdle();

        Assert.Same(lead, FocusManager.GetFocusedElement(rootScope)); // NOT the TabItem — the prime fenced itself off
        Assert.Same(lead, focus.FocusedElement);                      // actual focus untouched
    }

    // ── IsSelectable: a focusable-but-not-selectable command tab (the "never the selected tab" contract on a plain
    //    TabControl — the model, not just the input gates, must enforce it) ──────────────────────────────────────────

    private static (UITestHost Host, TabControl Tabs, TabItem Cmd, TabItem A, TabItem B) CommandTab()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        var cmd = new TabItem { Header = "Cmd", Content = "bodyCmd", IsSelectable = false }; // index 0, non-selectable
        var a = new TabItem { Header = "A", Content = "bodyA" };
        var b = new TabItem { Header = "B", Content = "bodyB" };
        var tabs = new TabControl();
        tabs.Items.Add(cmd);
        tabs.Items.Add(a);
        tabs.Items.Add(b);
        host.ShowRoot(tabs);
        host.RunUntilIdle();
        return (host, tabs, cmd, a, b);
    }

    [Fact] // C9.NS1: auto-select skips a non-selectable tab at index 0 and lands on the first selectable one
    public void C9_NS1_AutoSelectSkipsNonSelectable()
    {
        var (host, tabs, cmd, a, _) = CommandTab();
        using var _h = host;
        Assert.Equal(1, tabs.SelectedIndex); // A (index 1), not the non-selectable Cmd (index 0)
        Assert.True(a.IsSelected);
        Assert.False(cmd.IsSelected);
    }

    [Fact] // C9.NS2: programmatic SelectedIndex / SelectedItem onto a non-selectable tab is rejected (clamps to -1)
    public void C9_NS2_ProgrammaticSelectRejected()
    {
        var (host, tabs, cmd, a, _) = CommandTab();
        using var _h = host;
        Assert.Equal(1, tabs.SelectedIndex);

        tabs.SelectedIndex = 0; // the non-selectable index
        host.RunUntilIdle();
        Assert.False(cmd.IsSelected);
        Assert.NotEqual(0, tabs.SelectedIndex); // did NOT select it

        tabs.SelectedItem = cmd; // the non-selectable item
        host.RunUntilIdle();
        Assert.False(cmd.IsSelected);
        _ = a;
    }

    [Fact] // C9.NS3: setting a non-selectable container's IsSelected=true (binding / direct) is rejected and reverted
    public void C9_NS3_ContainerIsSelectedFoldRejected()
    {
        var (host, tabs, cmd, a, _) = CommandTab();
        using var _h = host;

        cmd.IsSelected = true; // fold attempt
        host.RunUntilIdle();

        Assert.False(cmd.IsSelected);   // driven back to false — container and model stay consistent
        Assert.True(a.IsSelected);      // the real selection is untouched
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [Fact] // C9.NS4: clicking a non-selectable tab FOCUSES it (reachable) but does not select it
    public void C9_NS4_ClickFocusesNotSelects()
    {
        var (host, tabs, cmd, a, _) = CommandTab();
        using var _h = host;

        Click(host, cmd);
        Assert.True(cmd.IsFocused);      // reachable/focusable
        Assert.False(cmd.IsSelected);    // …but not selected
        Assert.Equal(1, tabs.SelectedIndex);
        _ = a;
    }

    [Fact] // C9.NS5: a TabControl whose tabs are ALL non-selectable selects nothing (no band) — the degenerate case
    public void C9_NS5_AllNonSelectableSelectsNothing()
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(48, 16) });
        using var _h = host;
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "X", Content = "bodyX", IsSelectable = false });
        tabs.Items.Add(new TabItem { Header = "Y", Content = "bodyY", IsSelectable = false });
        host.ShowRoot(tabs);
        host.RunUntilIdle();

        Assert.True(tabs.SelectedIndex < 0); // nothing selectable → no selection
    }
}
