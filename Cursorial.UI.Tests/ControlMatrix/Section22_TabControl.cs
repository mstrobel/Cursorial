using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

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
        using var _h = host;

        Assert.Equal(0, ((TabControl)a.LogicalParent!).SelectedIndex);
        Assert.True(a.IsSelected);
        Assert.True(a.HasCustomPseudoClass(":selected"));
    }

    [Fact] // C9.3: SelectedContent shows the selected tab's content
    public void C9_3_SelectedContentReflectsSelection()
    {
        var (host, tabs, _, _, _) = ThreeTabs();
        using var _h = host;
        Assert.Equal("bodyA", tabs.SelectedContent); // auto-selected tab 0

        tabs.SelectedIndex = 2;
        host.RunUntilIdle();
        Assert.Equal("bodyC", tabs.SelectedContent); // switched
    }

    [Fact] // C9.4: clicking a tab header selects it (and deselects the others — single mode)
    public void C9_4_ClickSelects()
    {
        var (host, tabs, a, b, _) = ThreeTabs();
        using var _h = host;

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
        using var _h = host;
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
        using var _h = host;
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
        using var _h = host;
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
        using var _h = host;

        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.Same(b, tabs.SelectedItem);
    }

    [Fact] // C9.9: setting TabItem.IsSelected folds into the single-selection model (selecting one deselects the rest)
    public void C9_9_IsSelectedFoldsIntoModel()
    {
        var (host, tabs, a, _, c) = ThreeTabs();
        using var _h = host;
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
        using var _h = host;

        tabs.SelectedIndex = 1;
        host.RunUntilIdle();
        Assert.False(a.HasCustomPseudoClass(":selected"));
        Assert.True(b.HasCustomPseudoClass(":selected"));
        Assert.False(c.HasCustomPseudoClass(":selected"));
    }
}
