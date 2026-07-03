using System.Collections.ObjectModel;
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
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
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
        var scp = FindDescendant<ScrollContentPresenter>(lb)!;
        var tree = host.Application.WindowManager!.Tree!;

        var before = RealizedIndices(lb.ItemContainerGenerator, 1000);
        var rasterBefore = tree.GetScene(scp)?.RasterVersion ?? -1;

        scroll.VerticalOffset = 3; // within K = max(viewport, 8) — an in-band composite slide
        host.RunUntilIdle();

        var after = RealizedIndices(lb.ItemContainerGenerator, 1000);
        var rasterAfter = tree.GetScene(scp)?.RasterVersion ?? -1;

        Assert.Equal(3, scroll.VerticalOffset);
        Assert.Equal(before, after);             // zero realize churn
        Assert.Equal(rasterBefore, rasterAfter); // zero re-raster — the band scene is frozen (invariant 3)
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

    // ── audit-driven regression rows (V2 adversarial review) ──────────────────────────────────────────

    private static (UITestHost Host, ListBox List) MakeVirtualItems(object[] items, int rows = Rows)
    {
        var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, rows) });
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = items,
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);
        host.RunUntilIdle();
        return (host, lb);
    }

    [Fact] // VV2.5: realized containers arrange at content row index × avgItemRows (multi-row items — avg ≠ 1)
    public void VV2_5_TrueContentRowArrange()
    {
        // 4-row UIElement items ⇒ avgItemRows refines well above 1, so index × avg ≠ index.
        var (host, lb) = MakeVirtualItems(Enumerable.Range(0, 500).Select(i => (object)new TextBlock { Text = $"r{i}\nb\nc\nd" }).ToArray());
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        var c0 = gen.ContainerFromIndex(0)!;
        var c1 = gen.ContainerFromIndex(1)!;
        var c2 = gen.ContainerFromIndex(2)!;
        Assert.Equal(0, c0.Bounds.Row);
        Assert.True(c1.Bounds.Row > 1, $"c1.Row={c1.Bounds.Row} — avg should exceed 1 for multi-row items (kills top=index)");
        Assert.Equal(2 * c1.Bounds.Row, c2.Bounds.Row); // item i sits at i × avg
    }

    [Fact] // VV2.12: a structural Move / equal-Replace under virtualization reconciles the window (no blank row / stray)
    public void VV2_12_StructuralChange_ReconcilesWindow()
    {
        var src = new ObservableCollection<string>(Enumerable.Range(0, 1000).Select(i => $"item{i:0000}"));
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Size(24, 12) });
        var list = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = src,
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        host.ShowRoot(list);
        host.RunUntilIdle();
        var gen = list.ItemContainerGenerator;

        // Move an UNREALIZED item (800) into the band (index 5) — a Move changes no no-op-guard key, so the guard must
        // be busted explicitly or this is swallowed → blank band row.
        src.Move(800, 5);
        host.RunUntilIdle();
        Assert.NotNull(gen.ContainerFromIndex(5)); // realized — not a blank band row
        Assert.Equal("item0800", gen.ItemFromContainer(gen.ContainerFromIndex(5)!));

        // Equal-count Replace at a realized index (itemCount unchanged — another guard-busting case).
        src[6] = "REPLACED";
        host.RunUntilIdle();
        Assert.NotNull(gen.ContainerFromIndex(6));
        Assert.Equal("REPLACED", gen.ItemFromContainer(gen.ContainerFromIndex(6)!));
    }

    [Fact] // VV2.13: recycling a container that hosted a UIElement item survives a scroll far-and-back (no double-parent crash)
    public void VV2_13_RecycleUIElementContent_NoCrash()
    {
        var (host, lb) = MakeVirtualItems(Enumerable.Range(0, 2000).Select(i => (object)new TextBlock { Text = $"a{i}" }).ToArray());
        using var _ = host;
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        // Pre-fix: the 2nd re-anchor threw "TextBlock already has a visual parent (ContentPresenter)" when a recycled
        // container re-hosted a UIElement item whose visual parent the pooled container never released.
        var ex = Record.Exception(() =>
        {
            foreach (var offset in new[] { 1000, 0, 1000, 0 })
            {
                scroll.VerticalOffset = offset;
                host.RunUntilIdle();
            }
        });
        Assert.Null(ex);
    }
}
