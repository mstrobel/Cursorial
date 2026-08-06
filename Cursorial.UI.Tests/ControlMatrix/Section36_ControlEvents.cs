using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix — the event-exposure sweep: each newly-exposed routed / CLR event actually fires at its raise site
// (and Expander's pre-commit veto abandons the transition). Controls are hosted so RaiseEvent walks a live route.
public sealed class Section36_ControlEvents
{
    private static UIHeadlessHost Host() =>
        UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(40, 20) });

    [Fact] // Slider.ValueChanged (RangeValueChangedEventArgs old→new)
    public void Slider_ValueChanged()
    {
        using var host = Host();
        var slider = new Slider { Minimum = 0, Maximum = 100 };
        host.ShowRoot(slider);
        host.RunUntilIdle();

        RangeValueChangedEventArgs? last = null;
        slider.ValueChanged += (_, e) => last = e;

        slider.Value = 40;
        host.RunUntilIdle();

        Assert.NotNull(last);
        Assert.Equal(0, last!.OldValue);
        Assert.Equal(40, last.NewValue);
    }

    [Fact] // Expander.Expanding (pre-commit) then Expanded (post-commit); Collapsing then Collapsed on the way back
    public void Expander_ExpandingThenExpanded_AndCollapse()
    {
        using var host = Host();
        var expander = new Expander();
        host.ShowRoot(expander);
        host.RunUntilIdle();

        var order = new List<string>();
        expander.Expanding += (_, _) => order.Add("expanding");
        expander.Expanded += (_, _) => order.Add("expanded");
        expander.Collapsing += (_, _) => order.Add("collapsing");
        expander.Collapsed += (_, _) => order.Add("collapsed");

        expander.IsExpanded = true;
        host.RunUntilIdle();
        expander.IsExpanded = false;
        host.RunUntilIdle();

        Assert.Equal(new[] { "expanding", "expanded", "collapsing", "collapsed" }, order);
    }

    [Fact] // Expander.Expanding veto abandons the transition — IsExpanded stays false, Expanded never fires
    public void Expander_ExpandingVeto()
    {
        using var host = Host();
        var expander = new Expander();
        host.ShowRoot(expander);
        host.RunUntilIdle();

        expander.Expanding += (_, e) => e.Cancel = true;
        var expandedFired = false;
        expander.Expanded += (_, _) => expandedFired = true;

        expander.IsExpanded = true;
        host.RunUntilIdle();

        Assert.False(expander.IsExpanded); // veto abandoned the open
        Assert.False(expandedFired);
    }

    [Fact] // Expander.Expanding does NOT fire on a redundant set (already collapsed → set false)
    public void Expander_NoEventOnRedundantSet()
    {
        using var host = Host();
        var expander = new Expander();
        host.ShowRoot(expander);
        host.RunUntilIdle();

        var fired = false;
        expander.Collapsing += (_, _) => fired = true;
        expander.IsExpanded = false; // already false — no transition
        host.RunUntilIdle();

        Assert.False(fired);
    }

    [Fact] // ListBoxItem.Selected / Unselected
    public void ListBoxItem_SelectedUnselected()
    {
        using var host = Host();
        var item = new ListBoxItem { Content = "one" };
        host.ShowRoot(item);
        host.RunUntilIdle();

        bool selected = false, unselected = false;
        item.Selected += (_, _) => selected = true;
        item.Unselected += (_, _) => unselected = true;

        item.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(selected);

        item.IsSelected = false;
        host.RunUntilIdle();
        Assert.True(unselected);
    }

    [Fact] // TabItem.Selected / Unselected
    public void TabItem_SelectedUnselected()
    {
        using var host = Host();
        var item = new TabItem { Header = "tab" };
        host.ShowRoot(item);
        host.RunUntilIdle();

        bool selected = false, unselected = false;
        item.Selected += (_, _) => selected = true;
        item.Unselected += (_, _) => unselected = true;

        item.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(selected);

        item.IsSelected = false;
        host.RunUntilIdle();
        Assert.True(unselected);
    }

    [Fact] // ComboBoxItem.Selected (the audit scoped ComboBoxItem to Selected only)
    public void ComboBoxItem_Selected()
    {
        using var host = Host();
        var item = new ComboBoxItem { Content = "opt" };
        host.ShowRoot(item);
        host.RunUntilIdle();

        var selected = false;
        item.Selected += (_, _) => selected = true;

        item.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(selected);
    }

    [Fact] // ComboBox.DropDownOpened / DropDownClosed
    public void ComboBox_DropDownOpenedClosed()
    {
        using var host = Host();
        var combo = new ComboBox();
        combo.Items.Add("a");
        combo.Items.Add("b");
        host.ShowRoot(combo);
        host.RunUntilIdle();

        bool opened = false, closed = false;
        combo.DropDownOpened += (_, _) => opened = true;
        combo.DropDownClosed += (_, _) => closed = true;

        combo.IsDropDownOpen = true;
        host.RunUntilIdle();
        Assert.True(opened);

        combo.IsDropDownOpen = false;
        host.RunUntilIdle();
        Assert.True(closed);
    }

    [Fact] // TreeViewItem.Selected / Unselected (the container the audit missed; added for family parity)
    public void TreeViewItem_SelectedUnselected()
    {
        using var host = Host();
        var node = new TreeViewItem { Header = "n" };
        host.ShowRoot(node);
        host.RunUntilIdle();

        bool selected = false, unselected = false;
        node.Selected += (_, _) => selected = true;
        node.Unselected += (_, _) => unselected = true;

        node.IsSelected = true;
        host.RunUntilIdle();
        Assert.True(selected);

        node.IsSelected = false;
        host.RunUntilIdle();
        Assert.True(unselected);
    }

    [Fact] // IsSelectedChanged fires on every IsSelected change (both directions) across all four selectable containers
    public void ItemContainers_IsSelectedChanged()
    {
        using var host = Host();
        var list = new ListBoxItem { Content = "a" };
        var tab = new TabItem { Header = "t" };
        var combo = new ComboBoxItem { Content = "c" };
        var tree = new TreeViewItem { Header = "n" };
        var panel = new StackPanel();
        foreach (var e in new UIElement[] { list, tab, combo, tree })
            panel.Children.Add(e);
        host.ShowRoot(panel);
        host.RunUntilIdle();

        int lc = 0, tc = 0, cc = 0, trc = 0;
        list.IsSelectedChanged += (_, _) => lc++;
        tab.IsSelectedChanged += (_, _) => tc++;
        combo.IsSelectedChanged += (_, _) => cc++;
        tree.IsSelectedChanged += (_, _) => trc++;

        list.IsSelected = true; list.IsSelected = false;
        tab.IsSelected = true; tab.IsSelected = false;
        combo.IsSelected = true; combo.IsSelected = false;
        tree.IsSelected = true; tree.IsSelected = false;
        host.RunUntilIdle();

        Assert.Equal(2, lc); // true → false
        Assert.Equal(2, tc);
        Assert.Equal(2, cc);
        Assert.Equal(2, trc);
    }
}
