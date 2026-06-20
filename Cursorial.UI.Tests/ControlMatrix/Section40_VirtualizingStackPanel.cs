using System.Text;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix-virt §V2 — VirtualizingStackPanel (uniform-height item mode). The panel drives realization from its
// own MeasureOverride (sanctioned §5.3), realizing only the SCP band's worth of items, arranges them at true content
// rows, and reports the uniform extent through the V1 IScrollContentHost contract. The headline gate: an in-band
// scroll re-realizes nothing (invariant 3).
public sealed class Section40_VirtualizingStackPanel
{
    private const int Rows = 12;

    private static (UITestHost Host, ListBox List) MakeVirtual(int count, int rows = Rows)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, rows) });
        var lb = new ListBox
        {
            ItemsPanel = new FuncTemplateContent(_ => new VirtualizingStackPanel()),
            ItemsSource = Enumerable.Range(0, count).Select(i => $"item{i:0000}").ToArray(),
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true); // before ShowRoot — the panel reads it on connect
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb);
    }

    private static int CountRealized(ItemContainerGenerator gen, int n)
    {
        var c = 0;
        for (var i = 0; i < n; i++)
            if (gen.ContainerFromIndex(i) is not null)
                c++;
        return c;
    }

    private static HashSet<int> RealizedIndices(ItemContainerGenerator gen, int n)
    {
        var s = new HashSet<int>();
        for (var i = 0; i < n; i++)
            if (gen.ContainerFromIndex(i) is not null)
                s.Add(i);
        return s;
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

    private static string Screen(UITestHost host, int rows = Rows)
    {
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
            sb.AppendLine(host.GetRowText(r));
        return sb.ToString();
    }

    [Fact] // VV2.1/VV2.2: the VSP is a VirtualizingPanel + ILogicalScrollHost; attach enables virtualization
    public void VV2_1_PanelIdentity_AttachWiring()
    {
        Assert.True(typeof(VirtualizingPanel).IsAssignableFrom(typeof(VirtualizingStackPanel)));
        Assert.True(typeof(IScrollContentHost).IsAssignableFrom(typeof(VirtualizingStackPanel)));

        var (host, lb) = MakeVirtual(500);
        using var _ = host;
        Assert.True(lb.ItemContainerGenerator.IsVirtualizing); // the panel called EnableVirtualization on connect
        Assert.Equal(500, lb.ItemContainerGenerator.ContainerCount); // item-indexed (V0)
    }

    [Fact] // VV2.3/VV2.6: only the band's worth is realized (not all N), and the top items render
    public void VV2_3_BoundedRealization()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;

        var realized = CountRealized(lb.ItemContainerGenerator, 1000);
        Assert.True(realized is > 0 and < 200, $"realized={realized} (should be ~band+slack, not 1000)");
        Assert.Contains("item0000", Screen(host)); // the viewport top renders
    }

    [Fact] // VV2.4: extent == itemCount for uniform 1-row items (proportional without realizing all N)
    public void VV2_4_Extent_ItemCount()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        Assert.Equal(1000, ((IScrollContentHost)lb.ItemsHost!).GetExtent().Rows);
    }

    [Fact] // VV2.7: an in-band scroll (≤ K) re-realizes NOTHING — the realized set is unchanged (invariant 3)
    public void VV2_7_InBandScroll_NoChurn()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        var before = RealizedIndices(lb.ItemContainerGenerator, 1000);
        scroll.VerticalOffset = 3; // within K = max(viewport, 8) — an in-band composite slide
        host.RunUntilIdle();
        var after = RealizedIndices(lb.ItemContainerGenerator, 1000);

        Assert.Equal(3, scroll.VerticalOffset);
        Assert.Equal(before, after); // zero realize churn
    }

    [Fact] // VV2.8: a far scroll (re-anchor) realizes the new window + unrealizes the old
    public void VV2_8_ReAnchor_RealizesNewWindow()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        Assert.Null(gen.ContainerFromIndex(500)); // far item not realized at the top
        Assert.NotNull(gen.ContainerFromIndex(5)); // an old-window item IS realized at the top

        scroll.VerticalOffset = 500;
        host.RunUntilIdle();

        Assert.NotNull(gen.ContainerFromIndex(500)); // realized after the re-anchor
        Assert.Null(gen.ContainerFromIndex(5));      // a non-focused old-window item unrealized (item 0 stays — focus keep-alive)
        Assert.Contains("item0500", Screen(host));   // the new window renders
    }

    [Fact] // VV2.9: a short list realizes all + reports the EXACT realized sum (not the viewport)
    public void VV2_9_ShortList_ExactRows()
    {
        var (host, lb) = MakeVirtual(3);
        using var _ = host;
        Assert.Equal(3, CountRealized(lb.ItemContainerGenerator, 3));
        Assert.Equal(3, ((IScrollContentHost)lb.ItemsHost!).GetExtent().Rows); // exact, so no false overflow
    }

    [Fact] // VV2.11: selection of an off-screen (unrealized) index stays correct through the scroll
    public void VV2_11_OffScreenSelection()
    {
        var (host, lb) = MakeVirtual(1000);
        using var _ = host;

        lb.SelectedIndex = 700;
        host.RunUntilIdle();
        Assert.Equal(700, lb.SelectedIndex);
        Assert.Equal("item0700", lb.SelectedItem); // resolved from the source, not a (null) container (V0)

        var scroll = FindDescendant<ScrollViewer>(lb)!;
        scroll.VerticalOffset = 700;
        host.RunUntilIdle();
        Assert.Equal(700, lb.SelectedIndex); // unchanged by the scroll
        Assert.NotNull(lb.ItemContainerGenerator.ContainerFromIndex(700)); // now realized
    }
}
