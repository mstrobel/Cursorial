using System.Collections.ObjectModel;

using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

// ReSharper disable InconsistentNaming

namespace Cursorial.Tests.UI.ControlMatrix;

// Control-matrix-virt §V4 — variable-height sticky per-item cache + prefix-sum geometry. V2's single avgItemRows is
// replaced by a sticky per-item measured-height cache (survives unrealization) + a dense prefix-sum (monotone) giving
// O(1) cumulative tops / extent and an O(log n) EstimateItemAt binary search. A uniform list degenerates to V2 exactly.
public sealed class Section44_VirtualizationVariableHeight
{
    private const int Rows = 12;

    // Items whose container measures to a varying number of rows: item i is a TextBlock with linesFn(i) lines.
    private static (UIHeadlessHost Host, ListBox List) MakeVar(int count, Func<int, int> linesFn, int rows = Rows)
    {
        var items = Enumerable.Range(0, count)
            .Select(i => (object) new TextBlock { Text = MultiLine(i, linesFn(i)) })
            .ToArray();

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, rows) });
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

    private static string MultiLine(int i, int lines)
        => string.Join("\n", Enumerable.Range(0, Math.Max(1, lines)).Select(j => $"i{i:0000}_{j}"));

    private static ILogicalScrollHost Logical(ListBox lb) => (ILogicalScrollHost) lb.ItemsHost!;

    // Pump to idle while collecting any LayoutCycle / ResidualLayoutWork diagnostics (the real non-convergence signal —
    // the layout engine emits these, DEBUG-only, when the 16-pass fixpoint is exceeded; it never throws, and an
    // abandoned non-converged layout still reports idle, so a bool-only check can't see a crawl). Returns whether the
    // pump reached idle within the frame budget AND the collected cycle diagnostics.
    private static (bool Idle, List<LayoutDiagnosticEvent> Cycles) PumpCollectingCycles(UIHeadlessHost host, int maxFrames = 8)
    {
        var cycles = new List<LayoutDiagnosticEvent>();
        void OnDiag(LayoutDiagnosticEvent e)
        {
            if (e.Kind is LayoutDiagnosticKind.LayoutCycle or LayoutDiagnosticKind.ResidualLayoutWork)
                lock (cycles) cycles.Add(e);
        }

        LayoutDiagnostics.DiagnosticRaised += OnDiag;
        try
        {
            return (host.RunUntilIdle(maxFrames), cycles);
        }
        finally
        {
            LayoutDiagnostics.DiagnosticRaised -= OnDiag;
        }
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

    private static HashSet<int> RealizedIndices(ItemContainerGenerator gen, int n)
    {
        var s = new HashSet<int>();
        for (var i = 0; i < n; i++)
            if (gen.ContainerFromIndex(i) is not null)
                s.Add(i);
        return s;
    }

    [Fact] // §V2/V4 LineStep boundary (audit fix, commit 860434c): UP at an exact item top steps a WHOLE item back,
           // not a 1-cell nudge. LineStep was dead until the ScrollViewer wired it (V5/§V1) — the degenerate boundary
           // case surfaced only once a consumer existed; uniform 1-row items hid it (item step == cell step == 1).
    public void VV4_LineStep_UpFromItemBoundary_StepsWholeItem()
    {
        var (host, lb) = MakeVar(50, _ => 3); // multi-row items ⇒ an item step ≠ a cell step
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        IScrollContentHost host2 = Logical(lb);

        var top1 = gen.ContainerFromIndex(1)!.Bounds.Row; // item 1's true top (a multi-row offset)
        var top2 = gen.ContainerFromIndex(2)!.Bounds.Row;
        Assert.True(top1 > 1, $"need multi-row items for a meaningful boundary test (top1={top1})");

        // UP from an exact boundary (offset == item 1's top) → item 0's top (row 0): magnitude top1, NOT 1.
        Assert.Equal(top1, host2.LineStep(top1, -1, vertical: true));
        // DOWN from the same boundary → item 2's top: magnitude == item 1's height.
        Assert.Equal(top2 - top1, host2.LineStep(top1, +1, vertical: true));
        // UP from one cell INTO item 1 → snap to item 1's OWN top: magnitude 1.
        Assert.Equal(1, host2.LineStep(top1 + 1, -1, vertical: true));
    }

    [Fact] // VV4.2: heterogeneous items arrange at the CUMULATIVE sum of preceding heights (prefix-sum), not index × const.
    public void VV4_2_PrefixSumArrange()
    {
        var (host, lb) = MakeVar(500, i => (i % 3) + 1); // heights cycle 1,2,3,1,2,3…
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        // Walk the realized window from the top; each container's arranged top == the running sum of prior heights.
        var top = 0;
        var asserted = 0;
        for (var i = 0; i < 500; i++)
        {
            if (gen.ContainerFromIndex(i) is not { } c)
                break; // past the realized band
            Assert.Equal(top, c.Bounds.Row);
            top += c.DesiredSize.Rows;
            asserted++;
        }

        Assert.True(asserted >= 3, "expected several realized rows to validate the prefix-sum arrange");
        // Heights actually vary (otherwise this would also pass under the old uniform model).
        Assert.True(gen.ContainerFromIndex(0)!.DesiredSize.Rows != gen.ContainerFromIndex(2)!.DesiredSize.Rows);
    }

    [Fact] // VV4.5/VV4.4: EstimateItemAt is the consistent inverse of the prefix-sum offset map (monotone).
    public void VV4_5_EstimateItemAt_IsConsistentInverse()
    {
        var (host, lb) = MakeVar(500, i => (i % 3) + 1);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        var logical = Logical(lb);

        // For each realized item, EstimateItemAt(top-of-item) round-trips to the item, and the item-top boundary
        // resolves to the item itself (not the previous one).
        for (var i = 0; i < 500; i++)
        {
            if (gen.ContainerFromIndex(i) is not { } c)
                break;
            var top = logical.BringItemIntoView(i).Row;
            Assert.Equal(c.Bounds.Row, top);                  // BringItemIntoView == the arranged top (VV4.6)
            Assert.Equal(i, logical.EstimateItemAt(top));     // inverse round-trips
        }

        // Monotone: EstimateItemAt never decreases as the offset grows.
        var prev = 0;
        for (var off = 0; off < 60; off++)
        {
            var item = logical.EstimateItemAt(off);
            Assert.True(item >= prev, $"EstimateItemAt({off})={item} < prev {prev} — not monotone");
            prev = item;
        }
    }

    [Fact] // VV4.6: BringItemIntoView returns the item's true cell rect (cached height when measured).
    public void VV4_6_BringItemIntoView_TrueRect()
    {
        var (host, lb) = MakeVar(500, i => i == 1 ? 3 : 1); // item 1 is tall
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        var logical = Logical(lb);

        var c1 = gen.ContainerFromIndex(1)!;
        var rect = logical.BringItemIntoView(1);
        Assert.Equal(c1.Bounds.Row, rect.Row);
        Assert.Equal(c1.DesiredSize.Rows, rect.Rows);
        Assert.True(rect.Rows >= 3, $"item 1 should be ≥3 rows, was {rect.Rows}");
    }

    [Fact] // VV4.1: a measured item's height is sticky — it survives unrealization (extent geometry keeps using it).
    public void VV4_1_StickyCache_SurvivesUnrealization()
    {
        // Item 60 is distinctively tall; everything else is 1 row, so the estimate stays ~1.
        var (host, lb) = MakeVar(400, i => i == 60 ? 5 : 1);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        var logical = Logical(lb);
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        // Scroll item 60 into the band so it measures, and record its true height.
        scroll.VerticalOffset = 58;
        host.RunUntilIdle();
        Assert.NotNull(gen.ContainerFromIndex(60));
        var measured = logical.BringItemIntoView(60).Rows;
        Assert.True(measured >= 5, $"item 60 should measure ≥5 rows, was {measured}");

        // Scroll back to the top — item 60 unrealizes.
        scroll.VerticalOffset = 0;
        host.RunUntilIdle();
        Assert.Null(gen.ContainerFromIndex(60)); // truly unrealized

        // The cache is sticky: its height is still the measured value, NOT the ~1-row estimate.
        Assert.Equal(measured, logical.BringItemIntoView(60).Rows);
    }

    [Fact] // VV4.8: a realistic heterogeneous list converges under the LayoutManager fixpoint (no LayoutCycle diagnostic).
    public void VV4_8_Convergence_NoLayoutCycle()
    {
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, Rows) });
        using var _ = host;
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = Enumerable.Range(0, 1000).Select(i => (object) new TextBlock { Text = MultiLine(i, (i % 5) + 1) }).ToArray(),
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);

        // Real convergence gate: reach idle within a small frame budget AND emit no LayoutCycle/ResidualLayoutWork
        // (the layout engine never throws, and an abandoned crawl still reports idle, so both checks are needed).
        var (idle, cycles) = PumpCollectingCycles(host);
        Assert.True(idle, "heterogeneous list must reach idle within the frame budget");
        Assert.Empty(cycles);                                    // the refine→invalidate loop terminated under the fixpoint

        Assert.NotNull(lb.ItemContainerGenerator.ContainerFromIndex(0)); // the band realized
        var e1 = Logical(lb).GetExtent().Rows;
        Assert.True(host.RunUntilIdle(2));                       // a no-input pump is immediately idle (no residual work)
        Assert.Equal(e1, Logical(lb).GetExtent().Rows);         // extent stable (converged)
    }

    [Fact] // VV4.13: a bimodal list (a tall block among short items) scrolled deep converges + covers the band (no window crawl).
    public void VV4_13_BimodalDeepScroll_ConvergesAndCovers()
    {
        // Items 1500-1699 are 6-row blocks among 1-row items — a density contrast that, re-derived from the
        // band-perturbed global estimate each pass, crawled the window past the fixpoint (the audit repro). The
        // session expand-only window must keep it bounded.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, Rows) });
        using var _ = host;
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = Enumerable.Range(0, 2000)
                .Select(i => (object) new TextBlock { Text = MultiLine(i, i is >= 1500 and < 1700 ? 6 : 1) }).ToArray(),
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);
        host.RunUntilIdle();
        var gen = lb.ItemContainerGenerator;
        var logical = Logical(lb);
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        scroll.VerticalOffset = scroll.Extent.Rows / 2; // deep into / near the tall block
        var (idle, cycles) = PumpCollectingCycles(host);
        Assert.True(idle, "bimodal deep scroll must converge within the frame budget (no crawl)");
        Assert.Empty(cycles);

        // Coverage (VV2.6): the item at the top of the viewport is realized — no blank band row.
        var topItem = logical.EstimateItemAt(scroll.VerticalOffset);
        Assert.NotNull(gen.ContainerFromIndex(topItem));
    }

    [Fact] // VV4.15 (VV2.6 hardening): after a variable-height re-anchor, EVERY band row maps to a realized container.
    public void VV4_15_VariableHeightReAnchor_FullBandCoverage()
    {
        // A bimodal list (a tall block among 1-row items) so the global estimate mis-predicts the band's items at a
        // deep re-anchor — the case the reduced (const-2) realization slack must survive via the expand-only window.
        var (host, lb) = MakeVar(2000, i => i is >= 900 and < 1100 ? 5 : 1);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;
        var logical = Logical(lb);
        var scroll = FindDescendant<ScrollViewer>(lb)!;
        var scp = FindDescendant<ScrollContentPresenter>(lb)!;

        scroll.VerticalOffset = scroll.Extent.Rows / 2; // deep into/near the tall block
        host.RunUntilIdle();

        // Walk EVERY band row (not just the viewport top): the item at each row must be realized — no blank band row.
        for (var row = scp.BandStartRow; row < scp.BandStartRow + scp.BandLength; row++)
            Assert.NotNull(gen.ContainerFromIndex(logical.EstimateItemAt(row)));
    }

    [Fact] // VV4.14: 0-height (empty-content) items interspersed with real items don't defeat virtualization (estimate floor).
    public void VV4_14_ZeroHeightItems_StayVirtualized()
    {
        // Every 3rd item is empty (measures 0 rows); without flooring the never-measured fallback at 1, Estimate→<1
        // collapses the prefix and the window realizes far too much. The floor keeps the realized window band-sized.
        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, Rows) });
        using var _ = host;
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = Enumerable.Range(0, 3000).Select(i => (object) new TextBlock { Text = i % 3 == 0 ? "" : $"i{i}" }).ToArray(),
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);

        var (idle, cycles) = PumpCollectingCycles(host);
        Assert.True(idle);
        Assert.Empty(cycles);

        var realized = 0;
        for (var i = 0; i < 3000; i++)
            if (lb.ItemContainerGenerator.ContainerFromIndex(i) is not null)
                realized++;
        Assert.True(realized < 300, $"0-height items must not defeat virtualization — realized {realized}/3000"); // band ≈ 36 rows
    }

    [Fact] // VV4.9: after a re-anchor (far offset jump) the extent settles within the same idle pump.
    public void VV4_9_ThumbSettle_AfterReAnchor()
    {
        var (host, lb) = MakeVar(2000, i => (i % 4) + 1);
        using var _ = host;
        var scroll = FindDescendant<ScrollViewer>(lb)!;

        scroll.VerticalOffset = 900; // re-anchor far down
        host.RunUntilIdle();
        var settled = Logical(lb).GetExtent().Rows;

        host.RunUntilIdle(); // no input — must be a no-op
        Assert.Equal(settled, Logical(lb).GetExtent().Rows);
        Assert.True(settled >= 2000, $"extent {settled} should be ≥ itemCount for ≥1-row items");
    }

    [Fact] // VV4.11: an all-1-row list degenerates to V2's exact uniform behavior.
    public void VV4_11_UniformParity()
    {
        var (host, lb) = MakeVar(1000, _ => 1);
        using var _ = host;
        var gen = lb.ItemContainerGenerator;

        Assert.Equal(1000, Logical(lb).GetExtent().Rows); // exact itemCount × 1
        Assert.Equal(0, gen.ContainerFromIndex(0)!.Bounds.Row);
        Assert.Equal(1, gen.ContainerFromIndex(1)!.Bounds.Row); // item k at row k
        Assert.Equal(2, gen.ContainerFromIndex(2)!.Bounds.Row);
    }

    [Fact] // VV4.12: an in-band slide over a variable-height list still re-rasters nothing and re-realizes nothing.
    public void VV4_12_InBandSlide_NoChurn()
    {
        var (host, lb) = MakeVar(1000, i => (i % 3) + 1);
        using var _ = host;
        var scroll = FindDescendant<ScrollViewer>(lb)!;
        var scp = FindDescendant<ScrollContentPresenter>(lb)!;
        var tree = host.Application.WindowManager!.Tree!;

        var before = RealizedIndices(lb.ItemContainerGenerator, 1000);
        var rasterBefore = tree.GetScene(scp)?.RasterVersion ?? -1;
        var extentBefore = Logical(lb).GetExtent().Rows;

        scroll.VerticalOffset = 2; // within K — an in-band composite slide
        host.RunUntilIdle();

        Assert.Equal(2, scroll.VerticalOffset);
        Assert.Equal(before, RealizedIndices(lb.ItemContainerGenerator, 1000)); // zero realize churn
        Assert.Equal(rasterBefore, tree.GetScene(scp)?.RasterVersion ?? -1);    // zero re-raster (invariant 3)
        Assert.Equal(extentBefore, Logical(lb).GetExtent().Rows);              // extent unchanged (no re-measure)
    }

    [Fact] // VV4.10: a structural insert re-keys the sticky cache so the measured height follows its item.
    public void VV4_10_StructuralInsert_ReKeysCache()
    {
        var src = new ObservableCollection<object>(
            Enumerable.Range(0, 400).Select(i => (object) new TextBlock { Text = MultiLine(i, i == 30 ? 4 : 1) }));

        var host = UIHeadlessHost.Create(new UIHeadlessHostOptions { InitialSize = new Size(24, Rows) });
        using var _ = host;
        var lb = new ListBox
        {
            ItemsPanel = new ItemsPanelTemplate(_ => new VirtualizingStackPanel()),
            ItemsSource = src,
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        host.ShowRoot(lb);
        host.RunUntilIdle();
        var scroll = FindDescendant<ScrollViewer>(lb)!;
        var logical = Logical(lb);

        // Measure the tall item (30) so its height enters the sticky cache.
        scroll.VerticalOffset = 28;
        host.RunUntilIdle();
        var tallHeight = logical.BringItemIntoView(30).Rows;
        Assert.True(tallHeight >= 4);

        // Insert a 1-row item ahead of it: the tall item shifts 30 → 31, and the cache must follow it.
        src.Insert(10, new TextBlock { Text = "inserted" });
        host.RunUntilIdle();
        scroll.VerticalOffset = 28; // keep it in the band so it re-realizes at its new index
        host.RunUntilIdle();

        Assert.Equal(401, lb.ItemContainerGenerator.ContainerCount);
        Assert.Equal(tallHeight, logical.BringItemIntoView(31).Rows); // the tall height followed the item to 31
        Assert.Equal(1, logical.BringItemIntoView(30).Rows);          // index 30 is now the shifted 1-row neighbour
    }
}
