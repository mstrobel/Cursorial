// xUnit1031 (no blocking task ops) is deliberately disabled here: UIHeadlessHost is single-thread-
// affine — an async test method would resume off the UI thread and trip the affinity asserts, so
// these tests block on purpose (the blocked work is thread-pool-side and cannot deadlock).
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Rendering;
using Cursorial.Tests.UI.LayoutMatrix;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;

using Xunit.Abstractions;

namespace Cursorial.Tests.UI.Integration;

/// <summary>
/// DIAGNOSIS ONLY (scratch): reproduce the maintainer's symmetric negative-margin clip and identify
/// which rectangle does the cutting.
/// </summary>
public sealed class BoundaryClipDiagnosisTests(ITestOutputHelper output)
{
    private const int WindowColumns = 40;
    private const int WindowRows = 4;

    /// <summary>24 distinct glyphs, one per cell — the loss is unambiguous.</summary>
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWX";

    // ───────────────────────────── ① the repro: boundary CHILD (RichTextPresenter) ─────────────────────────────

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void RichTextPresenter_SymmetricNegativeMargin_UnderBoundaryParent(int margin, bool parentIsBoundary)
    {
        var text = new RichTextPresenter
                   {
                       Source = Alphabet,
                       Margin = new Margins(-margin, 0, -margin, 0),
                       HorizontalAlignment = HorizontalAlignment.Left,
                       VerticalAlignment = VerticalAlignment.Top
                   };

        // A panel sized to the child's DesiredSize (natural − 2·margin) and centred: exactly the
        // maintainer's shape (arrange slot 45 wide, child 57 wide at −6).
        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsRenderBoundary = parentIsBoundary
                    };
        panel.Children.Add(text);

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        text.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        output.WriteLine($"margin=-{margin}  parentIsBoundary={parentIsBoundary}");
        DumpGeometry(root, panel, text);
        DumpGrid(host);

        var row = host.GetRowText(0);
        var visible = row.Trim();
        output.WriteLine($"visible = \"{visible}\"  ({visible.Length} of {Alphabet.Length})");
        output.WriteLine(new string('─', 60));
    }

    // ───────────────────────────── ② the same shape with an INLINE (non-boundary) child ─────────────────────────────

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void InlineProbe_SymmetricNegativeMargin_UnderBoundaryParent(int margin, bool parentIsBoundary)
    {
        var probe = new Probe(Alphabet.Length, 1)
                    {
                        Margin = new Margins(-margin, 0, -margin, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        OnRender = (p, ctx) =>
                        {
                            for (var i = 0; i < p.Natural.Columns; i++)
                                ctx.Set(i, 0, Alphabet[i].ToString(), default);
                        }
                    };

        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsRenderBoundary = parentIsBoundary
                    };
        panel.Children.Add(probe);

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        output.WriteLine($"[inline probe] margin=-{margin}  parentIsBoundary={parentIsBoundary}");
        DumpGeometry(root, panel, probe);
        DumpGrid(host);

        var row = host.GetRowText(0);
        var visible = row.Trim();
        output.WriteLine($"visible = \"{visible}\"  ({visible.Length} of {Alphabet.Length})");
        output.WriteLine(new string('─', 60));
    }

    // ───────────────────────────── ③ which opt-in? ClipToBounds vs IsRenderBoundary vs Opacity ─────────────────────────────

    [Theory]
    [InlineData("none")]
    [InlineData("IsRenderBoundary")]
    [InlineData("ClipToBounds")]
    [InlineData("Opacity")]
    [InlineData("RenderOffset")]
    public void WhichParentPredicateClips(string predicate)
    {
        const int Margin = 6;

        var text = new RichTextPresenter
                   {
                       Source = Alphabet,
                       Margin = new Margins(-Margin, 0, -Margin, 0),
                       HorizontalAlignment = HorizontalAlignment.Left,
                       VerticalAlignment = VerticalAlignment.Top
                   };

        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top
                    };
        panel.Children.Add(text);

        switch (predicate)
        {
            case "IsRenderBoundary": panel.IsRenderBoundary = true; break;
            case "ClipToBounds":     panel.ClipToBounds = true; break;
            case "Opacity":          panel.Opacity = 0.99; break;
            case "RenderOffset":     panel.RenderOffsetColumn = 0; panel.IsRenderBoundary = true; break;
        }

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        text.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        output.WriteLine($"parent predicate = {predicate}");
        DumpGeometry(root, panel, text);
        DumpGrid(host);
        output.WriteLine($"visible = \"{host.GetRowText(0).Trim()}\"");
        output.WriteLine(new string('─', 60));
    }

    // ───────────────────────────── ④ where does the INLINE loss happen? (raster vs composite) ─────────────────────────────

    /// <summary>
    /// Dumps the boundary parent's ZONE SCENE for the inline case. The scene is the raster target;
    /// if the six left glyphs are already absent from it, the loss happened at RASTER time (surface
    /// extent) and no composite-time change can recover them.
    /// </summary>
    [Fact]
    public void InlineProbe_ParentZoneSceneContents()
    {
        const int Margin = 6;

        var probe = new Probe(Alphabet.Length, 1)
                    {
                        Margin = new Margins(-Margin, 0, -Margin, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        OnRender = (p, ctx) =>
                        {
                            for (var i = 0; i < p.Natural.Columns; i++)
                                ctx.Set(i, 0, Alphabet[i].ToString(), default);
                        }
                    };

        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsRenderBoundary = true
                    };
        panel.Children.Add(probe);

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var zone = panel.Zone!;
        var scene = zone.Scene!;

        output.WriteLine($"panel bounds  = {Describe(panel.Bounds)}");
        output.WriteLine($"probe bounds  = {Describe(probe.Bounds)}   (needs zone-local columns [-6, 18))");
        output.WriteLine($"panel scene   = {scene.Columns}x{scene.Rows}   <- the RASTER TARGET");
        output.WriteLine($"panel clip    = {zone.EffectiveClip}");

        var sceneRow = new StringBuilder();
        for (var column = 0; column < scene.Columns; column++)
        {
            var grapheme = scene.GetCell(column, 0).Grapheme;
            sceneRow.Append(string.IsNullOrEmpty(grapheme) || grapheme == " " ? '.' : grapheme[0]);
        }

        output.WriteLine($"scene row 0   = \"{sceneRow}\"");
        output.WriteLine($"window row 0  = \"{host.GetRowText(0).Trim()}\"");
    }

    // ───────────────────────────── ⑤ the group-surface containment constraint ─────────────────────────────

    [Fact]
    public void TranslucentParent_IsGroupRoot_AndItsSurfaceBoundsTheMembers()
    {
        const int Margin = 6;

        var text = new RichTextPresenter
                   {
                       Source = Alphabet,
                       Margin = new Margins(-Margin, 0, -Margin, 0),
                       HorizontalAlignment = HorizontalAlignment.Left,
                       VerticalAlignment = VerticalAlignment.Top
                   };

        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        Opacity = 0.5
                    };
        panel.Children.Add(text);

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        var zone = panel.Zone!;
        output.WriteLine($"panel.IsGroupRoot = {zone.IsGroupRoot}");
        output.WriteLine($"panel scene       = {zone.Scene?.Columns}x{zone.Scene?.Rows}");
        output.WriteLine($"panel group scene = {(zone.GroupScene is { } g ? $"{g.Columns}x{g.Rows}" : "<none>")}");
        output.WriteLine($"panel clip        = {zone.EffectiveClip}");
        output.WriteLine($"text  clip        = {text.Zone!.EffectiveClip}   scene={text.Zone.Scene!.Columns}x{text.Zone.Scene.Rows}");
        DumpGrid(host);
    }

    // ───────────────────────────── ⑥ the must-not-break pin: ScrollContentPresenter ─────────────────────────────

    [Fact]
    public void ScrollViewer_ClipsItsContentToTheViewport_Baseline()
    {
        var scroller = new ScrollViewer
                       {
                           HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                           VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                           Width = 12,
                           Height = 3,
                           HorizontalAlignment = HorizontalAlignment.Left,
                           VerticalAlignment = VerticalAlignment.Top,
                           Content = new Probe(200, 100) { FillGlyph = "S" }
                       };

        var root = new StackPanel();
        root.Children.Add(scroller);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());

        output.WriteLine($"scroller bounds = {Describe(scroller.Bounds)}");
        DumpGrid(host);

        // The pin: nothing paints outside the 12x3 viewport.
        for (var row = 0; row < WindowRows; row++)
        for (var column = 0; column < WindowColumns; column++)
        {
            var inViewport = column < 12 && row < 3;
            var grapheme = host.GetCell(column, row).Grapheme;
            var ink = !string.IsNullOrEmpty(grapheme) && grapheme != " ";
            if (!inViewport)
                Assert.False(ink, $"ink escaped the viewport at ({column}, {row}): '{grapheme}'");
        }
    }

    // ───────────────────────────── ⑦ the boundary child's OWN raster is complete ─────────────────────────────

    /// <summary>
    /// The counterpart to ④. A <see cref="RichTextPresenter"/> is its own boundary, so it rasters
    /// into a scene sized to its own (margin-expanded) bounds — all 24 glyphs are present. Only the
    /// COMPOSITE clip withheld them, which is why this half of the bug was recoverable at composite
    /// time — and now is: the clip is the presenter's own footprint and every glyph reaches the screen.
    /// </summary>
    [Fact]
    public void BoundaryChild_OwnSceneHoldsEverything_OnlyTheCompositeClipWithholdsIt()
    {
        const int Margin = 6;

        var text = new RichTextPresenter
                   {
                       Source = Alphabet,
                       Margin = new Margins(-Margin, 0, -Margin, 0),
                       HorizontalAlignment = HorizontalAlignment.Left,
                       VerticalAlignment = VerticalAlignment.Top
                   };

        var panel = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsRenderBoundary = true
                    };
        panel.Children.Add(text);

        var root = new StackPanel();
        root.Children.Add(panel);

        using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                               {
                                                   InitialSize = new Size(WindowColumns, WindowRows)
                                               });
        host.ShowRoot(root);
        Assert.True(host.RunUntilIdle());
        text.InvalidateMeasure();
        Assert.True(host.RunUntilIdle());

        var zone = text.Zone!;
        var scene = zone.Scene!;

        var sceneRow = new StringBuilder();
        for (var column = 0; column < scene.Columns; column++)
        {
            var grapheme = scene.GetCell(column, 0).Grapheme;
            sceneRow.Append(string.IsNullOrEmpty(grapheme) || grapheme == " " ? '.' : grapheme[0]);
        }

        output.WriteLine($"text scene     = {scene.Columns}x{scene.Rows}");
        output.WriteLine($"text scene row = \"{sceneRow}\"   <- COMPLETE");
        output.WriteLine($"text own footprint = Rect({scene.Columns}x{scene.Rows} @ ({zone.OffsetColumn}, {zone.OffsetRow}))");
        output.WriteLine($"text EffectiveClip = {zone.EffectiveClip}   <- its OWN footprint (was the parent's)");
        output.WriteLine($"parent EffectiveClip = {panel.Zone!.EffectiveClip}   <- no longer inherited: the panel never asked to clip");
        output.WriteLine($"window row 0   = \"{host.GetRowText(0).Trim()}\"");

        Assert.Equal(Alphabet, sceneRow.ToString());

        // The cut used to be byte-identical to the panel's clip. It is now the presenter's own
        // margin-expanded footprint, and every glyph the scene holds reaches the screen.
        Assert.NotEqual(panel.Zone.EffectiveClip, zone.EffectiveClip);
        Assert.Equal(new Rect(zone.OffsetColumn, zone.OffsetRow, scene.Columns, scene.Rows), zone.EffectiveClip);
        Assert.Equal(Alphabet, host.GetRowText(0).Trim());
    }

    // ───────────────────────────── ⑧ the loss used to track the margin, one for one ─────────────────────────────

    /// <summary>
    /// The sweep that originally pinned the arithmetic: the loss per side equalled the negative margin
    /// for every margin 1…8. <b>Fixed</b> — every glyph survives at every margin, and the sweep now
    /// pins the absence of a loss across the whole range rather than one lucky value.
    /// </summary>
    [Fact]
    public void NoLoss_AtAnyNegativeMargin()
    {
        for (var margin = 1; margin <= 8; margin++)
        {
            var text = new RichTextPresenter
                       {
                           Source = Alphabet,
                           Margin = new Margins(-margin, 0, -margin, 0),
                           HorizontalAlignment = HorizontalAlignment.Left,
                           VerticalAlignment = VerticalAlignment.Top
                       };

            var panel = new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Top,
                            IsRenderBoundary = true
                        };
            panel.Children.Add(text);

            var root = new StackPanel();
            root.Children.Add(panel);

            using var host = UIHeadlessHost.Create(new UIHeadlessHostOptions
                                                   {
                                                       InitialSize = new Size(WindowColumns, WindowRows)
                                                   });
            host.ShowRoot(root);
            Assert.True(host.RunUntilIdle());
            text.InvalidateMeasure();
            Assert.True(host.RunUntilIdle());

            var visible = host.GetRowText(0).Trim();
            var wouldHaveBeen = Alphabet.Substring(margin, Alphabet.Length - 2 * margin);
            output.WriteLine($"  margin=-{margin}  visible=\"{visible}\"  was-clipped-to=\"{wouldHaveBeen}\"  " +
                             $"lostLeft={Alphabet.IndexOf(visible[0])}  lostRight={Alphabet.Length - 1 - Alphabet.IndexOf(visible[^1])}");
            Assert.Equal(Alphabet, visible);
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private void DumpGeometry(UIElement root, UIElement panel, UIElement child)
    {
        output.WriteLine($"  root  bounds={Describe(root.Bounds)} desired={root.DesiredSize}  zone={Zone(root)}");
        output.WriteLine($"  panel bounds={Describe(panel.Bounds)} desired={panel.DesiredSize}  zone={Zone(panel)}");
        output.WriteLine($"  child bounds={Describe(child.Bounds)} desired={child.DesiredSize}  zone={Zone(child)}");
        output.WriteLine($"  child.ZoneRoot = {child.ZoneRoot?.GetType().Name ?? "<null>"}" +
                         $"   panel.ZoneRoot = {panel.ZoneRoot?.GetType().Name ?? "<null>"}");
    }

    private static string Describe(Rect bounds)
        => $"{bounds.Columns}x{bounds.Rows}@({bounds.Column},{bounds.Row})";

    private static string Zone(UIElement element)
    {
        if (element.Zone is not { } zone)
            return "<inline — no zone>";

        var scene = zone.Scene;
        return $"clip={zone.EffectiveClip} offset=({zone.OffsetColumn},{zone.OffsetRow}) " +
               $"scene={(scene is null ? "<none>" : $"{scene.Columns}x{scene.Rows}")} " +
               $"params.Clip={zone.Parameters.Clip}";
    }

    private void DumpGrid(UIHeadlessHost host)
    {
        var ruler = new StringBuilder("      ");
        for (var column = 0; column < WindowColumns; column++)
            ruler.Append(column % 10 == 0 ? (char)('0' + column / 10 % 10) : '·');
        output.WriteLine(ruler.ToString());

        for (var row = 0; row < WindowRows; row++)
        {
            var line = new StringBuilder($"  {row,2}  ");
            for (var column = 0; column < WindowColumns; column++)
            {
                var grapheme = host.GetCell(column, row).Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == " " ? '.' : grapheme[0]);
            }

            output.WriteLine(line.ToString());
        }
    }
}
