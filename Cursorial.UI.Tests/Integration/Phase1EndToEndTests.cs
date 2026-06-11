// xUnit1031 (no blocking task ops) is deliberately disabled here: UITestHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Diagnostics.CodeAnalysis;

using Cursorial.Drawing;
using Cursorial.Output;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Testing;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// Phase 1 integration: the design doc §14 P1 exit criteria proven end-to-end through
/// <see cref="UITestHost"/> — every assertion crosses the full spine (frame loop → layout →
/// render zones → compositor → retained buffer → FrameRenderer delta bytes), not a subsystem
/// harness. Covers: the static panel-tree showcase with cell + byte assertions, the
/// AffectsComposite re-emit-without-re-raster invariant (invariant 3), banded scrolling across a
/// re-anchor edge, mid-run resize, and the caret transform leg.
/// </summary>
[SuppressMessage("ReSharper", "UnusedTupleComponentInReturnValue")]
public sealed class Phase1EndToEndTests
{
    private static readonly Color Blue = Color.FromRgb(10, 20, 90);
    private static readonly Color Green = Color.FromRgb(20, 80, 20);
    private static readonly Color Red = Color.FromRgb(200, 30, 30);
    private static readonly Color HeaderFg = Color.FromRgb(230, 230, 240);
    private static readonly Color HeaderBg = Color.FromRgb(60, 60, 90);

    // ───────────────────────────── (a) the static panel tree ─────────────────────────────

    /// <summary>
    /// Builds the §14 showcase tree at the default 80×24 viewport:
    /// <code>
    /// Grid [1*, 3*]  →  columns 0–19 | 20–79
    /// ├─ col 0: DockPanel (blue bg) — "T"×2 rows docked top, "B" docked bottom, "F" fill (margin 1)
    /// └─ col 1: StackPanel (green bg)
    ///    ├─ "X" probe (10×1, left)                                row 0
    ///    ├─ Canvas (h 8) — "H" (z 1, doc-first) over "L" (z 0)    rows 1–8
    ///    ├─ opacity group (h 2, red bg, Opacity 0.5)              rows 9–10
    ///    └─ "S" probe (8×1, left, RenderOffsetColumn 3)           row 11
    /// </code>
    /// </summary>
    private static (Grid Root, StackPanel OpacityGroup, Probe Slide) BuildShowcaseTree()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star(3) });

        var dock = new DockPanel { Background = new SolidColorBrush(Blue) };
        var top = new Probe(20, 2)
        {
            OnRender = static (_, context) =>
            {
                var style = Style.Default.WithForeground(HeaderFg).WithBackground(HeaderBg);
                for (var row = 0; row < context.Size.Rows; row++)
                for (var column = 0; column < context.Size.Columns; column++)
                    context.Set(column, row, "T", style);
            }
        };
        DockPanel.SetDock(top, Dock.Top);
        var bottom = new Probe(20, 1) { FillGlyph = "B" };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var fill = new Probe(0, 0) { FillGlyph = "F", Margin = new Rendering.Margins(1) };
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(fill); // LastChildFill — takes the remaining rows 2–22

        var stack = new StackPanel { Background = new SolidColorBrush(Green) };
        stack.Children.Add(new Probe(10, 1) { FillGlyph = "X", HorizontalAlignment = HorizontalAlignment.Left });

        // ZIndex overlap: "H" comes FIRST in document order but carries the higher ZIndex — only
        // the (ZIndex, index) sort can put it on top of "L".
        var canvas = new Canvas { Height = 8 };
        var high = new Probe(6, 3) { FillGlyph = "H", ZIndex = 1 };
        Canvas.SetLeft(high, 6);
        Canvas.SetTop(high, 2);
        var low = new Probe(6, 3) { FillGlyph = "L" };
        Canvas.SetLeft(low, 2);
        Canvas.SetTop(low, 1);
        canvas.Children.Add(high);
        canvas.Children.Add(low);
        stack.Children.Add(canvas);

        // The opacity group: a sub-1 Opacity promotes it to a render boundary (predicate ②); its
        // red surface composites translucently over the green panel below it.
        var opacityGroup = new StackPanel
        {
            Height = 2,
            Opacity = 0.5,
            Background = new SolidColorBrush(Red)
        };
        stack.Children.Add(opacityGroup);

        // The RenderOffset slide: layout slot at columns 20–27, painted at columns 23–30.
        var slide = new Probe(8, 1)
        {
            FillGlyph = "S",
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderOffsetColumn = 3
        };
        stack.Children.Add(slide);

        Grid.SetColumn(dock, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(dock);
        grid.Children.Add(stack);
        return (grid, opacityGroup, slide);
    }

    [Fact]
    public void StaticPanelTree_CellAssertions_GlyphsAndStylesAtCoordinates()
    {
        using var host = UITestHost.Create();
        var (root, opacityGroup, _) = BuildShowcaseTree();
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        // Column 0 — the DockPanel: docked rows and the fill area with its margin ring.
        Assert.Equal("T", host.GetCell(5, 0).Grapheme);
        Assert.Equal(HeaderFg, host.GetCell(5, 0).Style.Foreground);
        Assert.Equal(HeaderBg, host.GetCell(5, 0).Style.Background);
        Assert.Equal("T", host.GetCell(19, 1).Grapheme);
        Assert.Equal("B", host.GetCell(5, 23).Grapheme);
        Assert.Equal("F", host.GetCell(5, 10).Grapheme);
        AssertBackgroundCell(host.GetCell(0, 2), Blue);   // fill's margin ring shows the panel surface
        AssertBackgroundCell(host.GetCell(19, 12), Blue);

        // Column 1 — the StackPanel surface and the left-aligned "X" row.
        Assert.Equal("X", host.GetCell(20, 0).Grapheme);
        Assert.Equal("X", host.GetCell(29, 0).Grapheme);
        AssertBackgroundCell(host.GetCell(35, 0), Green); // right of "X" — panel background
        AssertBackgroundCell(host.GetCell(50, 20), Green); // below all content

        // The Canvas overlap: "L" covers (22–27, 2–4), "H" covers (26–31, 3–5); in the overlap the
        // higher ZIndex wins even though "H" is FIRST in document order.
        Assert.Equal("L", host.GetCell(22, 2).Grapheme);
        Assert.Equal("L", host.GetCell(26, 2).Grapheme); // above H's top row — L's own cell
        Assert.Equal("H", host.GetCell(26, 3).Grapheme); // overlap — ZIndex 1 beats document order
        Assert.Equal("H", host.GetCell(27, 4).Grapheme); // overlap
        Assert.Equal("H", host.GetCell(31, 5).Grapheme); // H's own cell

        // The opacity group (rows 9–10): red at 50% over the green panel — the composited
        // background must land strictly between source and backdrop on every channel.
        var blended = host.GetCell(40, 9).Style.Background;
        Assert.Equal(ColorKind.Rgb, blended.Kind);
        AssertStrictlyBetween(Green.Red, Red.Red, blended.Red);
        AssertStrictlyBetween(Red.Green, Green.Green, blended.Green);
        AssertStrictlyBetween(Green.Blue, Red.Blue, blended.Blue);

        // The boundary published exactly the half opacity (0.5 → byte 128).
        var tree = host.Application.RenderSystem!.Tree!;
        Assert.Equal(128, tree.GetPublishedParameters(opacityGroup).Opacity);

        // The RenderOffset slide (row 11): layout slot columns 20–27, painted at 23–30; the
        // vacated columns show the parent zone's green surface through.
        Assert.Equal("S", host.GetCell(23, 11).Grapheme);
        Assert.Equal("S", host.GetCell(30, 11).Grapheme);
        AssertBackgroundCell(host.GetCell(20, 11), Green);
        AssertBackgroundCell(host.GetCell(22, 11), Green);
        AssertBackgroundCell(host.GetCell(31, 11), Green);
    }

    [Fact]
    public void StaticPanelTree_ByteStability_SecondFrameEmitsNothing()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var (root, _, _) = BuildShowcaseTree();
        host.ShowRoot(root);

        host.RunFrame();
        Assert.True(host.LastFrameBytes.Length > 0); // frame 1 — the full paint

        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // frame 2, nothing changed — zero bytes

        host.RunFrame();
        Assert.Equal(0, host.LastFrameBytes.Length); // and it stays quiet
    }

    // ───────────────────────────── (b) AffectsComposite invariant ─────────────────────────────

    [Fact]
    public void RenderOffsetChange_ReEmitsBytes_WithoutAnyReRaster()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var root = new Host();
        var probe = new Probe(6, 2)
        {
            FillGlyph = "Z",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderOffsetColumn = 1 // a boundary from the first frame (predicate ③)
        };
        root.Add(probe);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        Assert.True(string.IsNullOrEmpty(host.GetCell(0, 0).Grapheme)); // slot column 0 vacated
        Assert.Equal("Z", host.GetCell(1, 0).Grapheme);
        Assert.Equal("Z", host.GetCell(6, 1).Grapheme);

        var tree = host.Application.RenderSystem!.Tree!;
        var probeScene = tree.GetScene(probe);
        Assert.NotNull(probeScene);
        var probeVersion = probeScene.RasterVersion;
        var rootVersion = tree.GetScene(root)!.RasterVersion;
        var probeRenders = probe.RenderCount;
        var rootRenders = root.RenderCount;

        probe.RenderOffsetColumn = 2; // [AffectsComposite] — the slide
        host.RunFrame();

        // The terminal saw the move…
        Assert.True(host.LastFrameBytes.Length > 0);
        Assert.True(string.IsNullOrEmpty(host.GetCell(1, 0).Grapheme));
        Assert.Equal("Z", host.GetCell(2, 0).Grapheme);
        Assert.Equal("Z", host.GetCell(7, 1).Grapheme);

        // …but nothing re-rastered: no Render call anywhere, both cached scenes byte-identical
        // (invariant 3 — composite-lane changes re-emit from the cache).
        Assert.Equal(probeRenders, probe.RenderCount);
        Assert.Equal(rootRenders, root.RenderCount);
        Assert.Same(probeScene, tree.GetScene(probe));
        Assert.Equal(probeVersion, tree.GetScene(probe)!.RasterVersion);
        Assert.Equal(rootVersion, tree.GetScene(root)!.RasterVersion);
    }

    // ───────────────────────────── (c) banded scroll across a re-anchor edge ─────────────────────────────

    [Fact]
    public void BandedScroll_OneRowPerFrame_OffsetFramesCompositeOnly_ReAnchorReRastersTheBandZone()
    {
        // 20×10 viewport, 40 content rows ⇒ K = max(10, 8) = 10, band = min(40, 10 + 2·10) = 30
        // rows anchored at 0. Offsets 1…10 stay in-band; offset 11 re-anchors (|11 − 0| > 10) to
        // bandStart = clamp(11 − 10, 0, 40 − 30) = 1.
        using var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Rendering.Size(20, 10),
            CaptureFrameBytes = true
        });
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var stack = new StackPanel();
        var probes = new Probe[40];
        for (var i = 0; i < probes.Length; i++)
        {
            probes[i] = new Probe(20, 1) { FillGlyph = (i % 10).ToString() };
            stack.Children.Add(probes[i]);
        }

        presenter.Content = stack;
        root.Add(presenter);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var tree = host.Application.RenderSystem!.Tree!;
        var bandScene = tree.GetScene(presenter);
        Assert.NotNull(bandScene);
        Assert.Equal(30, bandScene.Rows); // band-sized, never extent-sized (LD13)
        var bandVersion = bandScene.RasterVersion;
        var rootVersion = tree.GetScene(root)!.RasterVersion;
        var renderSum = probes.Sum(p => p.RenderCount);

        // Offsets 1…10: pure composite slides — bytes flow, nothing re-rasters.
        for (var offset = 1; offset <= 10; offset++)
        {
            presenter.ScrollOffsetRow = offset;
            host.RunFrame();

            Assert.True(host.LastFrameBytes.Length > 0); // the slide reached the wire
            Assert.Equal(bandVersion, bandScene.RasterVersion);
            Assert.Equal(rootVersion, tree.GetScene(root)!.RasterVersion);
            Assert.Equal(renderSum, probes.Sum(p => p.RenderCount));
            Assert.Equal(0, presenter.BandStartRow);
            for (var row = 0; row < 10; row++)
                Assert.Equal(((row + offset) % 10).ToString(), host.GetCell(0, row).Grapheme);
        }

        // Offset 11: the re-anchor frame — exactly the band zone re-rasters (one bump on the band
        // scene, the root zone untouched); the zone rasters whole (probe-1 verdict), so every
        // content probe renders exactly once.
        presenter.ScrollOffsetRow = 11;
        host.RunFrame();

        Assert.Equal(1, presenter.BandStartRow);
        Assert.Same(bandScene, tree.GetScene(presenter)); // band length unchanged — scene reused
        Assert.Equal(bandVersion + 1, bandScene.RasterVersion);
        Assert.Equal(rootVersion, tree.GetScene(root)!.RasterVersion);
        Assert.All(probes, p => Assert.Equal(renderSum / probes.Length + 1, p.RenderCount));
        for (var row = 0; row < 10; row++)
            Assert.Equal(((row + 11) % 10).ToString(), host.GetCell(0, row).Grapheme);

        // And the frame after the re-anchor is quiet again — back to the composite-only regime.
        presenter.ScrollOffsetRow = 12;
        host.RunFrame();
        Assert.Equal(bandVersion + 1, bandScene.RasterVersion);
        for (var row = 0; row < 10; row++)
            Assert.Equal(((row + 12) % 10).ToString(), host.GetCell(0, row).Grapheme);
    }

    // ───────────────────────────── (d) resize mid-run ─────────────────────────────

    [Fact]
    public void ResizeMidRun_FullRelayout_SameFrame_CellsCorrectAtTheNewSize()
    {
        using var host = UITestHost.Create(new UITestHostOptions { CaptureFrameBytes = true });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition()); // 1*
        grid.ColumnDefinitions.Add(new ColumnDefinition()); // 1*
        var left = new Probe(0, 0) { FillGlyph = "A" };
        var right = new Probe(0, 0) { FillGlyph = "B" };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        host.ShowRoot(grid);
        Assert.True(host.RunUntilIdle());

        // 80×24: the star split is 40 | 40.
        Assert.Equal("A", host.GetCell(0, 0).Grapheme);
        Assert.Equal("A", host.GetCell(39, 23).Grapheme);
        Assert.Equal("B", host.GetCell(40, 0).Grapheme);
        Assert.Equal("B", host.GetCell(79, 23).Grapheme);

        var measures = left.MeasureOverrideCount;
        host.SendResize(60, 20);
        host.RunFrame(); // resize + relayout + repaint in ONE frame (doc §10.6)

        Assert.Equal(60, host.FrameBuffer.Columns);
        Assert.Equal(20, host.FrameBuffer.Rows);
        Assert.True(left.MeasureOverrideCount > measures); // full relayout ran
        Assert.True(host.LastFrameBytes.Length > 0);

        // 60×20: the star split is 30 | 30 and the content reaches the new edges.
        Assert.Equal("A", host.GetCell(0, 0).Grapheme);
        Assert.Equal("A", host.GetCell(29, 19).Grapheme);
        Assert.Equal("B", host.GetCell(30, 0).Grapheme);
        Assert.Equal("B", host.GetCell(59, 19).Grapheme);
    }

    // ───────────────────────────── (e) the caret leg ─────────────────────────────

    [Fact]
    public void Caret_PublishedInScrolledContent_BufferCursorLandsAtTheTransformedPosition()
    {
        using var host = UITestHost.Create(new UITestHostOptions { InitialSize = new Rendering.Size(20, 10) });
        var root = new Host();
        var presenter = new ScrollContentPresenter();
        var stack = new StackPanel();
        var probes = new Probe[40];
        for (var i = 0; i < probes.Length; i++)
        {
            probes[i] = new Probe(20, 1);
            stack.Children.Add(probes[i]);
        }

        presenter.Content = stack;
        root.Add(presenter);
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        Assert.False(host.FrameBuffer.CursorVisible);

        // Publish on content row 7, caret column 3 — unscrolled it lands at window (3, 7).
        host.Application.CaretService.Publish(probes[7], column: 3, row: 0, CursorShape.BlinkingBar);
        host.Application.RequestRender();
        host.RunFrame();
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(3, host.FrameBuffer.CursorColumn);
        Assert.Equal(7, host.FrameBuffer.CursorRow);
        Assert.Equal(CursorShape.BlinkingBar, host.FrameBuffer.CursorShape);

        // Scroll 5 rows: the transform folds the scroll — same publication, window row 2.
        presenter.ScrollOffsetRow = 5;
        host.RunFrame();
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(3, host.FrameBuffer.CursorColumn);
        Assert.Equal(2, host.FrameBuffer.CursorRow);

        // Scroll the owner above the viewport: clipped-out ⇒ the buffer cursor hides.
        presenter.ScrollOffsetRow = 12;
        host.RunFrame();
        Assert.False(host.FrameBuffer.CursorVisible);

        // And back into view.
        presenter.ScrollOffsetRow = 6;
        host.RunFrame();
        Assert.True(host.FrameBuffer.CursorVisible);
        Assert.Equal(1, host.FrameBuffer.CursorRow);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static void AssertBackgroundCell(Rendering.Cell cell, Color background)
    {
        Assert.True(string.IsNullOrEmpty(cell.Grapheme) || cell.Grapheme == " ",
                    $"Expected a surface cell, found glyph '{cell.Grapheme}'.");
        Assert.Equal(background, cell.Style.Background);
    }

    private static void AssertStrictlyBetween(byte low, byte high, byte actual)
    {
        Assert.InRange(actual, (byte)(Math.Min(low, high) + 1), (byte)(Math.Max(low, high) - 1));
    }
}
