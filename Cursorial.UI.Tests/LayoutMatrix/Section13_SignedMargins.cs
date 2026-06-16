using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Controls;

namespace Cursorial.Tests.UI.LayoutMatrix;

// ReSharper disable InconsistentNaming

/// <summary>
/// §13 — Signed margins (P2.6 — LD19): rows L219–L224, companions to the amended L37 (measure
/// math, in §2). Negative <c>Margin</c> components follow WPF semantics: the margin-deflate
/// enlarges, <c>DesiredSize</c> clamps ≥ 0, and the arrange position fold is signed — carried by
/// the signed <see cref="LayoutRect"/>. The LD3 alignment-offset clamp stays; margins are the only
/// layout-side source of negative placement.
/// </summary>
public class Section13_SignedMargins
{
    [Fact]
    public void L219_NegativeTopMargin_SignedPositionFold_OriginAboveSlot()
    {
        var probe = new Probe(4, 2)
        {
            Margin = new Margins(0, -1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        probe.Measure(new Size(20, 20));
        Assert.Equal(new Size(4, 1), probe.DesiredSize); // 2 rows + (−1) margin

        probe.Arrange(new Rect(0, 3, 10, 6));
        Assert.Equal(new LayoutRect(0, 2, 4, 2), probe.Bounds); // slot row 3 + margin.Top (−1)
    }

    [Fact]
    public void L220_StackPanel_PullUp_BoundsOverlap_LaterArrangedPaintsOver()
    {
        var a = new Probe(10, 2) { FillGlyph = "a" };
        var b = new Probe(10, 1) { FillGlyph = "b", Margin = new Margins(0, -1, 0, 0) };
        var c = new Probe(10, 1) { FillGlyph = "c" };
        var panel = new StackPanel();
        panel.Children.Add(a);
        panel.Children.Add(b);
        panel.Children.Add(c);

        var (_, tree) = LayoutFixture.CreateRenderRoot(panel, 10, 6);

        Assert.Equal(new Size(10, 0), b.DesiredSize); // the L37 clamp
        Assert.Equal(new LayoutRect(0, 0, 10, 2), a.Bounds);
        Assert.Equal(new LayoutRect(0, 1, 10, 1), b.Bounds); // overlaps A's last row
        Assert.Equal(new LayoutRect(0, 2, 10, 1), c.Bounds); // the stack advance consumed B's 0-row desired

        tree.Render();
        var view = new CellBufferView(tree.Composite(10, 6));
        Assert.Equal("a", view[0, 0].Grapheme);
        Assert.Equal("b", view[0, 1].Grapheme); // later document order paints over A's row 1
        Assert.Equal("c", view[0, 2].Grapheme);
    }

    [Fact]
    public void L221_NegativeLeftRightMargins_WidenBeyondSlot_OriginNegative()
    {
        var probe = new Probe(4, 2)
        {
            Margin = new Margins(-1, 0, -1, 0),
            VerticalAlignment = VerticalAlignment.Top // H stays Stretch
        };

        probe.Measure(new Size(20, 20));
        probe.Arrange(new Rect(0, 0, 10, 6));

        Assert.Equal(new Size(12, 2), Assert.Single(probe.ArrangeSizes)); // size_a = the widened inner slot
        Assert.Equal(new LayoutRect(-1, 0, 12, 2), probe.Bounds);
    }

    [Fact]
    public void L222_NegativeOriginAtZoneEdge_ClipsAboveTheZone()
    {
        var child = new Probe(4, 2)
        {
            Margin = new Margins(0, -1, 0, 0),
            OnRender = static (_, context) =>
            {
                for (var row = 0; row < context.Size.Rows; row++)
                for (var column = 0; column < context.Size.Columns; column++)
                    context.Set(column, row, row == 0 ? "x" : "y", default);
            }
        };
        var panel = new StackPanel();
        panel.Children.Add(child);

        var (_, tree) = LayoutFixture.CreateRenderRoot(panel, 10, 4);
        Assert.Equal(new LayoutRect(0, -1, 10, 2), child.Bounds); // negative origin at the zone's top edge

        tree.Render(); // no throw — the per-cell push-stack clip absorbs the off-zone row
        var view = new CellBufferView(tree.Composite(10, 4));
        for (var column = 0; column < 10; column++)
            Assert.Equal("y", view[column, 0].Grapheme); // local row 1 lands on zone row 0; local row 0 clipped
        Assert.Null(view[0, 1].Grapheme); // nothing wrapped or bled below
    }

    [Fact]
    public void L223_HitTest_NegativeOriginBounds_LocalTransformSubtractsOrigin()
    {
        (int Column, int Row)? observed = null;
        var child = new Probe(4, 2)
        {
            Margin = new Margins(-1, 0, -1, 0),
            HitTestCoreOverride = (column, row) =>
            {
                observed = (column, row);
                return true;
            }
        };
        var panel = new StackPanel();
        panel.Children.Add(child);

        var (_, tree) = LayoutFixture.CreateRenderRoot(panel, 10, 4);
        Assert.Equal(new LayoutRect(-1, 0, 12, 2), child.Bounds);
        tree.Render();

        Assert.Same(child, tree.HitTest(0, 0));
        Assert.Equal((1, 0), observed); // local = window − bounds origin: 0 − (−1) = 1
        Assert.Same(child, tree.HitTest(9, 0));
        Assert.Equal((10, 0), observed);
        Assert.Same(panel, tree.HitTest(0, 2)); // below the child — falls through to the panel

        // The coordinate-translation walks fold the signed origin (P2.6 review #7).
        Assert.Equal((-1, 0), child.TranslateToWindow(0, 0));
        Assert.Equal((1, 0), child.TranslateToLocal(0, 0));
        var (windowColumn, windowRow) = child.TranslateToWindow(2, 1);
        Assert.Equal((2, 1), child.TranslateToLocal(windowColumn, windowRow)); // round-trip identity
    }

    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void L224_AlignmentOffsetClampStays_NegativeEntersOnlyViaMargin(HorizontalAlignment alignment)
    {
        var probe = new Probe(4, 2)
        {
            MinWidth = 12,
            Margin = new Margins(-1, 0, 0, 0),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Top
        };

        probe.Measure(new Size(40, 40));
        probe.Arrange(new Rect(0, 0, 10, 6));

        // Inner slot 11 < used 12: the LD3 offset clamps to 0 (overflow pins left, WPF would center
        // negative); the −1 origin comes solely from the margin.Left term of the position fold.
        Assert.Equal(new LayoutRect(-1, 0, 12, 2), probe.Bounds);
    }

    [Fact]
    public void L225_DesiredSizeFloorEngaged_ArrangeUsesTheCachedNaturalSize()
    {
        // Margin more negative than the natural extent: desired rows clamp to 0 (L37), and a
        // `DesiredSize − margin` reconstruction would "recover" |margin| = 3 phantom rows. The
        // arrange content size must be the cached natural size (WPF's _unclippedDesiredSize).
        var probe = new Probe(4, 1)
        {
            Margin = new Margins(0, -3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };

        probe.Measure(new Size(20, 20));
        Assert.Equal(new Size(4, 0), probe.DesiredSize); // 1 row + (−3) margin floors at 0

        probe.Arrange(new Rect(0, 5, 10, 6));
        Assert.Equal(new Size(4, 1), Assert.Single(probe.ArrangeSizes)); // natural, not natural + 2
        Assert.Equal(new LayoutRect(0, 2, 4, 1), probe.Bounds);          // no phantom rows painted
    }
}
