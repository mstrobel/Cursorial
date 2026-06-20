using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix-virt §V3 (a) — selection + caret across the realization boundary. Reconcile-on-realize re-applies a
// selected-but-unrealized item's IsSelected when it scrolls into view; caret keep-alive pins a container that owns a
// live terminal-caret publication (a TextBox editing in a virtualized item survives a scroll-out). Keyboard-nav
// realize-then-focus is the V3b follow-on.
public sealed class Section41_VirtualizationSelectionCaret
{
    private static (UITestHost Host, ListBox List) MakeVirtual(int count, int rows = 12)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, rows) });
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = Enumerable.Range(0, count).Select(i => $"item{i:0000}").ToArray(),
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb);
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

    [Fact] // VV3.1: a selected-but-unrealized item shows IsSelected the moment it scrolls into view (reconcile-on-realize)
    public void VV3_1_ReconcileOnRealize()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        lb.SelectedIndex = 700;
        host.RunUntilIdle();
        Assert.Equal(700, lb.SelectedIndex);
        Assert.Null(gen.ContainerFromIndex(700)); // off-screen — not realized, so nothing visually shows selected yet

        var scroll = FindDescendant<ScrollViewer>(lb)!;
        scroll.VerticalOffset = 700;
        host.RunUntilIdle();

        var c = (ListBoxItem)gen.ContainerFromIndex(700)!;
        Assert.True(c.IsSelected); // the materializing container had its selection re-applied from the model
        Assert.True(c.HasCustomPseudoClass(":selected"));
    }

    [Fact] // VV3.1b: a NON-selected item realizes unselected (the reconcile drives FROM the model, not stale state)
    public void VV3_1b_NonSelectedRealizesUnselected()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        lb.SelectedIndex = 700;
        host.RunUntilIdle();
        var scroll = FindDescendant<ScrollViewer>(lb)!;
        scroll.VerticalOffset = 700;
        host.RunUntilIdle();
        Assert.True(((ListBoxItem)gen.ContainerFromIndex(700)!).IsSelected);

        // A neighbour that materializes in the same window is NOT selected.
        Assert.False(((ListBoxItem)gen.ContainerFromIndex(701)!).IsSelected);
    }

    [Fact] // VV3.2: caret keep-alive — a container owning a live caret publication survives UnrealizeRange
    public void VV3_2_CaretKeepAlive()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        var c = gen.ContainerFromIndex(3)!; // a realized container (in the top band)
        host.Application.CaretService.Publish(c, 0, 0, CursorShape.Default); // a caret within c's subtree (c itself)

        gen.UnrealizeRange(0, 12); // scroll-out the band — c must be PINNED by its caret
        Assert.Same(c, gen.ContainerFromIndex(3)); // kept alive (not unrealized)
        Assert.Null(gen.ContainerFromIndex(5));     // a neither-caret-nor-focused neighbour unrealizes (item 0 stays — focus)

        host.Application.CaretService.Clear(c); // caret gone → no longer pinned
        gen.UnrealizeRange(0, 12);
        Assert.Null(gen.ContainerFromIndex(3)); // now unrealizes
    }

    [Fact] // VV3.2b: the caret keep-alive honors a publication owned by a DESCENDANT of the container
    public void VV3_2b_CaretKeepAlive_Descendant()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        var c = gen.ContainerFromIndex(4)!;
        var descendant = FindDescendant<ContentPresenter>(c); // the ListBoxItem's content host (a visual descendant)
        Assert.NotNull(descendant);
        host.Application.CaretService.Publish(descendant!, 0, 0, CursorShape.Default);

        gen.UnrealizeRange(0, 12);
        Assert.Same(c, gen.ContainerFromIndex(4)); // pinned by the descendant's caret

        host.Application.CaretService.Clear(descendant!);
    }

    [Fact] // VV3.3 (V3b): keyboard nav to an OFF-BAND item scrolls it in, realizes it, then focuses it (realize-then-focus)
    public void VV3_3_KeyboardNav_OffBand_RealizesThenFocuses()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        gen.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();
        Assert.Null(gen.ContainerFromIndex(999)); // the last item is far off-band — not realized

        host.SendKey(Key.End); // jump to the last item: it must scroll into the window, realize, then focus
        host.RunUntilIdle();

        Assert.Equal(999, lb.SelectedIndex);
        var last = gen.ContainerFromIndex(999);
        Assert.NotNull(last);                          // the off-band target materialized
        Assert.True(((ListBoxItem) last!).IsFocused);  // …and received focus once realized (parked-focus completion)
    }

    [Fact] // VV3.3b: PageDown in a virtualized list pages by a viewport's worth of items, realizing across the boundary
    public void VV3_3b_PageDown_Virtualized_PagesAcrossBoundary()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        gen.ContainerFromIndex(0)!.Focus();
        host.RunUntilIdle();
        var page = lb.ItemsPerPage();
        Assert.True(page >= 2);

        host.SendKey(Key.PageDown);
        host.RunUntilIdle();
        Assert.Equal(page, lb.SelectedIndex);          // paged down by one viewport
        Assert.NotNull(gen.ContainerFromIndex(page));  // the landed item is realized
    }
}
