using System.Collections.ObjectModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix P9 §C2 — the ItemsControl pipeline (ItemContainerGenerator / ItemsPresenter / punch-43).
public sealed class Section15_Items
{
    private static (UITestHost Host, T Control) Show<T>(T control) where T : UIElement
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(30, 10) });
        host.ShowRoot(control);
        host.RunUntilIdle();
        return (host, control);
    }

    private static ItemContainerGenerator Gen(ItemsControl ic) => ic.ItemContainerGenerator;

    [Fact] // C2.1: a bound ItemsSource realizes one container per item
    public void ItemsSource_RealizesContainerPerItem()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new ObservableCollection<string> { "a", "b", "c" } });
        using var _ = host;

        Assert.NotNull(Gen(ic).ContainerFromIndex(0));
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
        Assert.NotNull(Gen(ic).ContainerFromIndex(2));
        Assert.Null(Gen(ic).ContainerFromIndex(3));
    }

    [Fact] // C2.1b: the direct Items lane realizes containers too
    public void DirectItems_RealizesContainerPerItem()
    {
        var ic = new ItemsControl();
        ic.Items.Add("x");
        ic.Items.Add("y");
        var (host, _) = Show(ic);
        using var __ = host;

        Assert.NotNull(Gen(ic).ContainerFromIndex(0));
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
    }

    [Fact] // C2.3: setting both Items (populated) and ItemsSource throws
    public void ItemsSourceAndItems_MutuallyExclusive()
    {
        var ic = new ItemsControl();
        ic.Items.Add("x");
        Assert.Throws<InvalidOperationException>(() => ic.ItemsSource = new[] { "a" });

        var ic2 = new ItemsControl { ItemsSource = new[] { "a" } };
        Assert.Throws<InvalidOperationException>(() => ic2.Items.Add("y")); // mutate Items while ItemsSource set
    }

    [Fact] // C2.4: the default container is a ContentPresenter
    public void DefaultContainer_IsContentPresenter()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" } });
        using var _ = host;
        Assert.IsType<ContentPresenter>(Gen(ic).ContainerFromIndex(0));
    }

    [Fact] // C2.5: a UIElement item is its own container (no presenter wrapper)
    public void UIElementItem_IsItsOwnContainer()
    {
        var leaf = new TextBlock("inline");
        var (host, ic) = Show(new ItemsControl { ItemsSource = new object[] { leaf } });
        using var _ = host;
        Assert.Same(leaf, Gen(ic).ContainerFromIndex(0)); // used directly, not wrapped
    }

    [Fact] // C2.6/C2.7 (punch 43): the container is a LOGICAL child of the ItemsControl, a VISUAL child of the panel
    public void Container_LogicalOfControl_VisualOfPanel()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" } });
        using var _ = host;

        var container = Gen(ic).ContainerFromIndex(0)!;
        Assert.Same(ic, container.LogicalParent);          // logical parent = the ItemsControl ⇒ inheritance flows from it
        Assert.IsType<StackPanel>(container.VisualParent); // visual parent = the (default) items panel
        Assert.NotSame(ic, container.VisualParent);
    }

    [Fact] // C2.8: Add at an index realizes one container; later indices shift
    public void AddAtIndex_RealizesAndShifts()
    {
        var source = new ObservableCollection<string> { "a", "c" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var c0 = Gen(ic).ContainerFromIndex(0);

        source.Insert(1, "b");
        host.RunUntilIdle();
        Assert.Same(c0, Gen(ic).ContainerFromIndex(0)); // index 0 unchanged
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));   // new container at 1
        Assert.NotNull(Gen(ic).ContainerFromIndex(2));   // "c" shifted to 2
    }

    [Fact] // C2.9: Remove unrealizes — the container's logical parent is cleared (the 4-step retraction ran)
    public void Remove_Unrealizes_ClearsLogicalParent()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var removed = Gen(ic).ContainerFromIndex(0)!;

        source.RemoveAt(0);
        host.RunUntilIdle();
        Assert.Null(removed.LogicalParent);              // logical detach ran
        Assert.Null(removed.VisualParent);               // visual detach ran
        Assert.Equal(-1, Gen(ic).IndexFromContainer(removed));
        Assert.NotNull(Gen(ic).ContainerFromIndex(0));   // "b" survived at 0
    }

    [Fact] // C2.11: Move reorders the same containers (no realize/unrealize)
    public void Move_ReordersSameContainers()
    {
        var source = new ObservableCollection<string> { "a", "b", "c" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;
        var a = Gen(ic).ContainerFromIndex(0)!;
        var b = Gen(ic).ContainerFromIndex(1)!;

        source.Move(0, 2); // a → end
        host.RunUntilIdle();
        Assert.Same(b, Gen(ic).ContainerFromIndex(0));   // b shifted up
        Assert.Same(a, Gen(ic).ContainerFromIndex(2));   // a at the end — same instance, not re-realized
    }

    [Fact] // C2.13: Reset (clear) unrealizes everything
    public void Reset_UnrealizesAll()
    {
        var source = new ObservableCollection<string> { "a", "b" };
        var (host, ic) = Show(new ItemsControl { ItemsSource = source });
        using var _ = host;

        source.Clear();
        host.RunUntilIdle();
        Assert.Null(Gen(ic).ContainerFromIndex(0));
    }

    [Fact] // C2.14: ItemContainerStyle applies to each container at the Explicit layer
    public void ItemContainerStyle_AppliedToContainers()
    {
        var style = new Style(); // selector-less explicit style
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a" }, ItemContainerStyle = style });
        using var _ = host;
        Assert.Same(style, Gen(ic).ContainerFromIndex(0)!.Style);
    }

    [Fact] // C2.15: a runtime ItemTemplate change re-realizes (the v1 Reset policy — containers are replaced)
    public void RuntimeItemTemplateChange_ReRealizes()
    {
        var (host, ic) = Show(new ItemsControl { ItemsSource = new[] { "a", "b" } });
        using var _ = host;
        var before = Gen(ic).ContainerFromIndex(0);

        ic.ItemTemplate = new DataTemplate { Content = new FuncTemplateContent(_ => new TextBlock("templated")) };
        host.RunUntilIdle();
        Assert.NotSame(before, Gen(ic).ContainerFromIndex(0)); // re-realized
        Assert.NotNull(Gen(ic).ContainerFromIndex(1));
    }

    [Fact] // C2.18: HeaderedItemsControl exposes Header + the items host (MenuItem's base — smoke)
    public void HeaderedItemsControl_HasHeaderAndItems()
    {
        var hic = new HeaderedItemsControl { Header = "File" };
        hic.Items.Add("Open");
        Assert.Equal("File", hic.Header);
        Assert.NotNull(hic.ItemContainerGenerator.ContainerFromIndex(0));
    }
}
