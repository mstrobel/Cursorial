using System.Buffers;

using Cursorial.Drawing;
using Cursorial.Drawing.Charts;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Content;
using Cursorial.Rendering.Fragments;
using Cursorial.Rendering.Text;

using static Cursorial.Rendering.CellBuffer;

namespace Cursorial.Tests.Drawing;

// P2.5 push-stack full coverage: the clip + translate stack is honored by EVERY DrawingContext draw
// path — deferred pen strokes (box + braille), chart braille and markers, titled boxes/panels, drop
// and inner shadows, formatted text, and embedded content (cells + fragments). Deferred records
// capture the ambient state at RECORD time, so junctions and gradients resolve in final scene
// coordinates. DrawHarness.Render composites over an opaque base, so clipped-away cells read back as
// the base color / a blank grapheme.
public class PushStackCoverageTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Black = Color.FromRgb(0, 0, 0);

    private static bool IsBraille(Cell cell) =>
        cell.Grapheme is [>= '⠀' and <= '⣿'];

    // ---- deferred box strokes -------------------------------------------------------------------

    [Fact]
    public void DrawBox_UnderTranslate_ShiftsTheOutline()
    {
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.PushTranslate(2, 1))
                ctx.DrawBox(new Rect(0, 0, 3, 3), White);
        });
        Assert.Equal("┌", b[2, 1].Grapheme);   // local (0,0) → scene (2,1)
        Assert.Equal("┘", b[4, 3].Grapheme);   // local (2,2) → scene (4,3)
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));   // nothing at the untranslated corner
    }

    [Fact]
    public void DrawBox_UnderClip_DropsCellsOutsideTheClip()
    {
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 3, 3)))
                ctx.DrawBox(new Rect(0, 0, 6, 5), White);
        });
        Assert.Equal("┌", b[0, 0].Grapheme);                    // top-left corner inside the clip
        Assert.Equal("─", b[2, 0].Grapheme);                    // top edge runs to the clip edge
        Assert.True(string.IsNullOrEmpty(b[3, 0].Grapheme));    // beyond the clip → nothing
        Assert.True(string.IsNullOrEmpty(b[5, 4].Grapheme));    // the far corner is clipped away
    }

    [Fact]
    public void DrawLine_UnderClip_RunsToTheClipEdge()
    {
        // A clipped line ends in a plain ─ at the boundary (the cell keeps its arm toward the clipped
        // neighbor), matching how a scene-edge-clipped stroke has always rendered.
        var b = DrawHarness.Render(8, 2, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 4, 2)))
                ctx.DrawLine(0, 0, 7, 0, White);
        });
        Assert.Equal("─", b[0, 0].Grapheme);
        Assert.Equal("─", b[3, 0].Grapheme);                    // boundary cell — still a full run glyph
        Assert.True(string.IsNullOrEmpty(b[4, 0].Grapheme));    // first clipped cell
    }

    [Fact]
    public void DrawBox_NegativeTranslate_OnSceneRemainderRendersCorrectly()
    {
        var b = DrawHarness.Render(8, 4, ctx =>
        {
            using (ctx.PushTranslate(-2, -2))
                ctx.DrawBox(new Rect(0, 0, 4, 4), White);   // top-left 2×2 of the box is off-scene
        });
        Assert.Equal("┘", b[1, 1].Grapheme);                    // local (3,3) → scene (1,1)
        Assert.Equal("─", b[0, 1].Grapheme);                    // bottom edge's on-scene remainder
        Assert.Equal("│", b[1, 0].Grapheme);                    // right edge's on-scene remainder
    }

    [Fact]
    public void Strokes_SameFigure_DifferentTranslates_JunctionFormsInSceneCoordinates()
    {
        // The figure/junction contract under transforms: two strokes recorded under DIFFERENT ambient
        // translates but in ONE figure must junction where they cross in FINAL scene coordinates.
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.BeginFigure())
            {
                using (ctx.PushTranslate(2, 0))
                    ctx.DrawLine(0, 2, 4, 2, White);   // scene (2,2)–(6,2) horizontal
                using (ctx.PushTranslate(0, 1))
                    ctx.DrawLine(4, 1, 4, 3, White);   // scene (4,2)–(4,4) vertical
            }
        });
        Assert.Equal("┬", b[4, 2].Grapheme);   // crossing at scene (4,2): left+right+down
        Assert.Equal("│", b[4, 3].Grapheme);
        Assert.Equal("─", b[3, 2].Grapheme);
    }

    [Fact]
    public void StrokeGradient_TravelsWithTheTranslate()
    {
        // A translated stroke must sample its brush exactly as the same stroke drawn untranslated —
        // record-time capture, local-frame sampling (gradients travel with the content).
        var pen = new Pen(new LinearGradientBrush(Red, Blue, RelativePoint.Left, RelativePoint.Right));

        var translated = DrawHarness.Render(10, 4, ctx =>
        {
            using (ctx.PushTranslate(4, 2))
                ctx.DrawLine(0, 0, 3, 0, pen);
        });
        var reference = DrawHarness.Render(10, 4, ctx => ctx.DrawLine(0, 0, 3, 0, pen));

        for (int i = 0; i < 4; i++)
            Assert.Equal(reference[i, 0].Style.Foreground, translated[4 + i, 2].Style.Foreground);
    }

    [Fact]
    public void ExplicitFigureBounds_AreTakenInCurrentLocalCoordinates()
    {
        // BeginFigure(bounds) under a translate: the explicit brush bounds map through the ambient
        // translate, so the gradient spans the figure where it actually lands on the scene.
        var pen = new Pen(new LinearGradientBrush(Red, Blue, RelativePoint.Left, RelativePoint.Right));

        var b = DrawHarness.Render(12, 3, ctx =>
        {
            using (ctx.PushTranslate(4, 0))
            using (ctx.BeginFigure(new Rect(0, 0, 4, 1)))
                ctx.DrawLine(0, 0, 3, 0, pen);
        });

        var left = b[4, 0].Style.Foreground;
        var right = b[7, 0].Style.Foreground;
        Assert.True(left.Red > left.Blue, $"figure's left end should be red-dominant, was {left}");
        Assert.True(right.Blue > right.Red, $"figure's right end should be blue-dominant, was {right}");
    }

    [Fact]
    public void DrawLine_NestedPush_ComposesTranslates()
    {
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.PushTranslate(1, 1))
            using (ctx.PushTranslate(2, 1))
                ctx.DrawLine(0, 0, 2, 0, White);   // total translate (3,2)
        });
        Assert.Equal("─", b[3, 2].Grapheme);
        Assert.Equal("─", b[5, 2].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
        Assert.True(string.IsNullOrEmpty(b[2, 1].Grapheme));
    }

    // ---- braille (diagonal lines + charts) ------------------------------------------------------

    [Fact]
    public void DiagonalLine_UnderTranslate_RastersAtTheTranslatedCells()
    {
        var b = DrawHarness.Render(10, 8, ctx =>
        {
            using (ctx.PushTranslate(4, 3))
                ctx.DrawLine(0, 0, 3, 3, White);   // diagonal → braille, scene cells (4,3)…(7,6)
        });
        Assert.True(IsBraille(b[4, 3]), $"expected braille at (4,3), got '{b[4, 3].Grapheme}'");
        Assert.True(IsBraille(b[7, 6]), $"expected braille at (7,6), got '{b[7, 6].Grapheme}'");
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));   // nothing at the untranslated origin
    }

    [Fact]
    public void DiagonalLine_UnderClip_PlotsOnlyInsideTheClip()
    {
        var b = DrawHarness.Render(8, 8, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 2, 2)))
                ctx.DrawLine(0, 0, 6, 6, White);
        });
        Assert.True(IsBraille(b[0, 0]));
        Assert.True(IsBraille(b[1, 1]));
        for (int i = 2; i < 7; i++)
            Assert.True(string.IsNullOrEmpty(b[i, i].Grapheme), $"cell ({i},{i}) should be clipped");
    }

    [Fact]
    public void LineChart_Braille_UnderViewportPush_ClipsToTheViewport()
    {
        // The "give this widget a viewport" call over a chart: the braille curve translates with the
        // content and paints only inside the clip. The y=x curve occupies the area's bottom-left /
        // top-right diagonal; the clip windows the bottom-left quadrant (local cols 0..3, rows 2..3 →
        // scene cols 2..5, rows 3..4), where the rising curve is present.
        var clip = new Rect(2, 3, 4, 2);
        var b = DrawHarness.Render(12, 6, ctx =>
        {
            using (ctx.Push(clip: clip, translateColumns: 2, translateRows: 1))
                ctx.LineChart(new Rect(0, 0, 8, 4), [new PointD(0, 0), new PointD(7, 7)], White);
        });

        bool anyInside = false;
        for (int r = 0; r < 6; r++)
        for (int c = 0; c < 12; c++)
        {
            bool inside = clip.Contains(c, r);
            if (!inside)
                Assert.True(string.IsNullOrEmpty(b[c, r].Grapheme), $"cell ({c},{r}) escaped the viewport");
            else if (IsBraille(b[c, r]))
                anyInside = true;
        }
        Assert.True(anyInside, "the curve should be visible inside the viewport");
    }

    [Fact]
    public void ScatterChart_Markers_UnderTranslate_FollowTheContent()
    {
        var b = DrawHarness.Render(10, 6, ctx =>
        {
            using (ctx.PushTranslate(3, 2))
                ctx.ScatterChart(new Rect(0, 0, 4, 3), [new PointD(0, 0), new PointD(3, 2)], White, MarkerStyle.Dot);
        });

        // (0,0) projects to the area's bottom-left cell, local (0,2) → scene (3,4); (3,2) to the
        // top-right, local (3,0) → scene (6,2).
        Assert.False(string.IsNullOrEmpty(b[3, 4].Grapheme));
        Assert.False(string.IsNullOrEmpty(b[6, 2].Grapheme));
        Assert.True(string.IsNullOrEmpty(b[0, 2].Grapheme));
    }

    [Fact]
    public void Sparkline_UnderTranslate_FollowsTheContent()
    {
        var b = DrawHarness.Render(10, 4, ctx =>
        {
            using (ctx.PushTranslate(2, 1))
                ctx.Sparkline(0, 0, 4, [1.0, 2.0, 3.0, 4.0], White);
        });
        Assert.False(string.IsNullOrEmpty(b[2, 1].Grapheme));
        Assert.False(string.IsNullOrEmpty(b[5, 1].Grapheme));
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
    }

    // ---- titled boxes / panels ------------------------------------------------------------------

    [Fact]
    public void DrawTitledBox_UnderTranslate_MovesOutlineAndTitleAsAUnit()
    {
        var b = DrawHarness.Render(16, 6, ctx =>
        {
            using (ctx.PushTranslate(3, 2))
                ctx.DrawTitledBox(new Rect(0, 0, 10, 3), new PanelTitle("Hi"), White);
        });

        Assert.Equal("┌", b[3, 2].Grapheme);     // outline translated
        Assert.Equal("┘", b[12, 4].Grapheme);
        Assert.Equal("H", b[6, 2].Grapheme);     // title: gap starts at local 2 → pad at scene (5,2), text at (6,2)
        Assert.Equal("i", b[7, 2].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
    }

    [Fact]
    public void DrawPanel_UnderClip_FillAndOutlineAreBounded()
    {
        var clip = new Rect(0, 0, 5, 3);
        var b = DrawHarness.Render(12, 6, ctx =>
        {
            using (ctx.PushClip(clip))
                ctx.DrawPanel(new Rect(0, 0, 10, 5), White, new SolidColorBrush(Red));
        });

        Assert.Equal("┌", b[0, 0].Grapheme);
        Assert.Equal(Red, b[2, 1].Style.Background);                   // interior fill inside the clip
        Assert.True(string.IsNullOrEmpty(b[9, 4].Grapheme));           // far corner clipped
        Assert.Equal(Black, b[6, 1].Style.Background);                 // fill outside the clip → base
        Assert.True(string.IsNullOrEmpty(b[5, 0].Grapheme));           // top edge stops at the clip
    }

    // ---- shadows --------------------------------------------------------------------------------

    [Fact]
    public void DropShadow_UnderTranslate_BandMovesWithTheElement()
    {
        var b = DrawHarness.Render(12, 8, ctx =>
        {
            using (ctx.PushTranslate(3, 1))
                ctx.DrawDropShadow(new Rect(2, 2, 3, 2), ShadowGeometry.Drop(radius: 1, strength: 0.5), Black);
        }, baseBackground: White);

        // Translate (3,1) moves the element to scene (5,3)-(7,4); its soft halo rides along. (8,4) is just
        // right of the translated element → shadow; the untranslated element's location stays base white.
        Assert.True(b[8, 4].Style.Background.Red < 255, "the shadow should darken the translated band");
        Assert.Equal(White, b[2, 2].Style.Background);   // the untranslated element/shadow location is now empty
        Assert.Equal(White, b[5, 3].Style.Background);   // inside the translated element → occluded
    }

    [Fact]
    public void DropShadow_UnderClip_IsSuppressedOutsideTheClip()
    {
        var element = new Rect(2, 2, 3, 2);
        var b = DrawHarness.Render(10, 8, ctx =>
        {
            using (ctx.PushClip(element))   // the clip admits only the element's own footprint
                ctx.DrawDropShadow(element, ShadowGeometry.Drop(radius: 1, strength: 0.5), Black);
        }, baseBackground: White);

        for (int r = 0; r < 8; r++)
        for (int c = 0; c < 10; c++)
            Assert.Equal(White, b[c, r].Style.Background);   // every shadow cell fell outside the clip
    }

    [Fact]
    public void InnerShadow_UnderTranslate_DarkensTheTranslatedEdges()
    {
        var fill = Color.FromRgb(150, 150, 150);
        var b = DrawHarness.Render(12, 8, ctx =>
        {
            using (ctx.PushTranslate(2, 1))
            {
                ctx.FillRectangle(new Rect(0, 0, 6, 5), fill);
                ctx.DrawInnerShadow(new Rect(0, 0, 6, 5), ShadowGeometry.Inner(radius: 1, strength: 0.5), Black);
            }
        }, baseBackground: White);

        Assert.True(b[2, 1].Style.Background.Red < fill.Red, "translated edge cell darkened below the fill");
        Assert.Equal(fill, b[4, 3].Style.Background);    // translated interior beyond the radius keeps the fill
        Assert.Equal(White, b[0, 0].Style.Background);   // the untranslated rect is untouched
    }

    // ---- formatted text -------------------------------------------------------------------------

    // "aaaa bbbb" word-wrapped at 4 → two lines: "aaaa" / "bbbb".
    private static FormattedText TwoLines() =>
        new TextFormatter().Format(new RichTextBuilder().Run("aaaa bbbb").Build(), 4);

    [Fact]
    public void FormattedText_UnderTranslate_PaintsAtTheTranslatedCells()
    {
        var b = DrawHarness.Render(10, 5, ctx =>
        {
            using (ctx.PushTranslate(3, 1))
                ctx.DrawFormattedText(TwoLines(), new Rect(0, 0, 6, 2), OutputCapabilities.None);
        });
        Assert.Equal("a", b[3, 1].Grapheme);
        Assert.Equal("b", b[3, 2].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
    }

    [Fact]
    public void FormattedText_UnderClip_CellsOutsideStayAtBase()
    {
        var b = DrawHarness.Render(10, 4, ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 2, 1)))
                ctx.DrawFormattedText(TwoLines(), new Rect(0, 0, 6, 2), OutputCapabilities.None);
        });
        Assert.Equal("a", b[0, 0].Grapheme);
        Assert.Equal("a", b[1, 0].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[2, 0].Grapheme));   // column past the clip
        Assert.True(string.IsNullOrEmpty(b[0, 1].Grapheme));   // second line entirely clipped
    }

    [Fact]
    public void FormattedText_NegativeTranslate_ScrollsTheTopLineOff()
    {
        // The scrolled-document case the v1 stack couldn't express: line 0 maps above the scene and is
        // clipped per cell; line 1 lands on row 0.
        var b = DrawHarness.Render(10, 3, ctx =>
        {
            using (ctx.PushTranslate(0, -1))
                ctx.DrawFormattedText(TwoLines(), new Rect(0, 0, 6, 2), OutputCapabilities.None);
        });
        Assert.Equal("b", b[0, 0].Grapheme);                    // line 1 ("bbbb") shows at the top
        Assert.True(string.IsNullOrEmpty(b[0, 1].Grapheme));    // nothing painted below it
    }

    [Fact]
    public void FormattedText_NestedPush_ComposesClipAndTranslate()
    {
        var b = DrawHarness.Render(10, 5, ctx =>
        {
            using (ctx.PushTranslate(1, 1))
            using (ctx.Push(clip: new Rect(1, 1, 3, 1), translateColumns: 1, translateRows: 0))
                ctx.DrawFormattedText(
                    new TextFormatter().Format(new RichTextBuilder().Run("xyzw").Build(), 4),
                    new Rect(0, 1, 4, 1), OutputCapabilities.None);
        });

        // Effective translate (2,1); effective clip = scene (2,2,3,1). "xyzw" at local (0,1) → scene
        // row 2, columns 2..5 — 'x','y','z' inside the clip, 'w' clipped.
        Assert.Equal("x", b[2, 2].Grapheme);
        Assert.Equal("z", b[4, 2].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[5, 2].Grapheme));
    }

    [Fact]
    public void FormattedText_DocumentBrush_SamplesInLocalCoordinates()
    {
        // The brushed overload under a translate: the gradient spans the document where the AUTHOR put
        // it (local frame), exactly as if the document were painted untranslated — then moved.
        var brush = new LinearGradientBrush(Red, Blue, RelativePoint.Left, RelativePoint.Right);
        var text = new TextFormatter().Format(new RichTextBuilder().Run("mmmmmmmm").Build(), 8);

        var translated = DrawHarness.Render(14, 4, ctx =>
        {
            using (ctx.PushTranslate(4, 2))
                ctx.DrawFormattedText(text, new Rect(0, 0, 8, 1), brush, OutputCapabilities.None);
        });
        var reference = DrawHarness.Render(14, 4, ctx =>
            ctx.DrawFormattedText(text, new Rect(0, 0, 8, 1), brush, OutputCapabilities.None));

        for (int i = 0; i < 8; i++)
            Assert.Equal(reference[i, 0].Style.Foreground, translated[4 + i, 2].Style.Foreground);
    }

    // ---- embedded content (cells + fragments) ---------------------------------------------------

    [Fact]
    public void Content_CellFallback_UnderPush_IsTranslatedAndClipped()
    {
        var clip = new Rect(2, 1, 3, 1);
        var b = DrawHarness.Render(10, 4, ctx =>
        {
            using (ctx.Push(clip: clip, translateColumns: 2, translateRows: 1))
                ctx.DrawContent(new Rect(0, 0, 5, 1), new CellContent("V"), OutputCapabilities.None);
        });

        Assert.Equal("V", b[2, 1].Grapheme);                    // local (0,0) → scene (2,1)
        Assert.Equal("V", b[4, 1].Grapheme);
        Assert.True(string.IsNullOrEmpty(b[5, 1].Grapheme));    // past the clip
        Assert.True(string.IsNullOrEmpty(b[0, 0].Grapheme));
    }

    [Fact]
    public void ContentFragment_UnderTranslate_AnchorsAtTheTranslatedCell()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 5);
        scene.Draw(ctx =>
        {
            using (ctx.PushTranslate(3, 2))
                ctx.DrawContent(new Rect(1, 1, 2, 1), new FragmentHost(frag), OutputCapabilities.None);
        });

        Assert.True(scene.Buffer.AsView().TryGetFragmentAnchor(frag.Key, out var anchor));
        Assert.Equal((4, 3), anchor);   // (1,1) + translate (3,2)
    }

    [Fact]
    public void ContentFragment_StraddlingTheClip_IsCropped_WhenTheProtocolCan()
    {
        var frag = new CroppableFragment(4, 2);
        using var scene = Scene.Create(10, 5);
        scene.Draw(ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 2, 4)))
                ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentHost(frag), OutputCapabilities.None);
        });

        var fragments = scene.Buffer.AsView().Fragments;
        int fragmentCount = fragments.Count;   // ref-struct dict — can't use Assert.Single
        Assert.Equal(1, fragmentCount);
        foreach (var (anchor, entry) in fragments)
        {
            Assert.Equal((0, 0), anchor);
            Assert.Equal(new Size(2, 2), entry.Fragment.GetSize());   // body cropped to the clip
        }
    }

    [Fact]
    public void ContentFragment_StraddlingTheClip_IsSuppressed_WhenItCannotCrop()
    {
        IBufferFragment frag = new FakeFragment(4, 2);   // default Clip → null
        using var scene = Scene.Create(10, 5);
        scene.Draw(ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 2, 4)))
                ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentHost(frag), OutputCapabilities.None);
        });

        int fragmentCount = scene.Buffer.AsView().Fragments.Count;   // ref-struct dict — can't use Assert.Empty
        Assert.Equal(0, fragmentCount);                               // suppression beats overdraw past the clip
    }

    [Fact]
    public void ContentFragment_AnchorOutsideTheClip_IsDropped()
    {
        IBufferFragment frag = new FakeFragment(2, 1);
        using var scene = Scene.Create(10, 5);
        scene.Draw(ctx =>
        {
            using (ctx.PushClip(new Rect(0, 0, 3, 4)))
                ctx.DrawContent(new Rect(5, 0, 2, 1), new FragmentHost(frag), OutputCapabilities.None);
        });

        int fragmentCount = scene.Buffer.AsView().Fragments.Count;   // ref-struct dict — can't use Assert.Empty
        Assert.Equal(0, fragmentCount);
    }

    [Fact]
    public void PreexistingFragment_IsNotCroppedByALaterPushedDraw()
    {
        // The crop pass touches only the fragments the pushed paint itself registered.
        IBufferFragment big = new FakeFragment(4, 2);
        IBufferFragment small = new FakeFragment(1, 1);
        using var scene = Scene.Create(10, 5);
        scene.Draw(ctx =>
        {
            ctx.DrawContent(new Rect(0, 0, 4, 2), new FragmentHost(big), OutputCapabilities.None);   // no push
            using (ctx.PushClip(new Rect(0, 3, 2, 2)))
                ctx.DrawContent(new Rect(0, 3, 1, 1), new FragmentHost(small), OutputCapabilities.None);
        });

        var view = scene.Buffer.AsView();
        Assert.True(view.TryGetFragmentAnchor(big.Key, out var bigAnchor));
        Assert.Equal((0, 0), bigAnchor);
        Assert.True(view.Fragments.TryGetValue(bigAnchor, out var bigEntry));
        Assert.Equal(new Size(4, 2), bigEntry.Fragment.GetSize());   // untouched by the pushed draw's crop
        Assert.True(view.ContainsFragment(small.Key));
    }

    // ---- fills: clip pre-clamp ------------------------------------------------------------------

    [Fact]
    public void Fills_UnderPush_OversizedRegionCostsOnlyTheVisibleCells()
    {
        // The transformed fill paths pre-clamp iteration to the clip mapped back into local space.
        // A Rect-max region (65535×65535) must cost O(visible): without the clamp this test iterates
        // ~4.3G cells per fill and effectively hangs; with it, the loop body runs ≤ scene-size times.
        var huge = new Rect(0, 0, ushort.MaxValue, ushort.MaxValue);

        var b = DrawHarness.Render(
            8,
            4,
            ctx =>
            {
                using (ctx.Push(clip: new Rect(1, 1, 3, 2), translateColumns: 1, translateRows: 1))
                {
                    ctx.FillRectangle(huge, Red);
                    ctx.FillOpaque(new Rect(1, 0, ushort.MaxValue, ushort.MaxValue), Blue);
                }
            },
            baseBackground: White);

        Assert.Equal(Red, b[1, 1].Style.Background);          // FillRectangle painted the clip's top-left
        Assert.Equal(Blue, b[2, 1].Style.Background);         // FillOpaque (local col ≥ 1 → scene col ≥ 2) over it
        Assert.Equal(DurableEmptyGrapheme, b[2, 1].Grapheme); // …as an occluding space cell
        Assert.Equal(Blue, b[3, 2].Style.Background);         // the clip's bottom-right corner
        Assert.Equal(White, b[4, 1].Style.Background);        // past the clip — untouched
        Assert.Equal(White, b[0, 0].Style.Background);        // before the clip — untouched
    }

    // ---- test fakes -----------------------------------------------------------------------------

    // An IContent that writes a glyph into every cell of its bounds (the cell fallback path).
    private sealed class CellContent(string glyph) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => availableSpace;

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            for (int r = bounds.Row; r < bounds.RowEnd; r++)
            for (int c = bounds.Column; c < bounds.ColumnEnd; c++)
                buffer.Set(c, r, glyph, Style.Default.WithForeground(Color.FromRgb(255, 255, 255)));
            return bounds;
        }
    }

    // An IContent that registers a fragment at its paint anchor (stands in for Image/Icon/ScaledText).
    private sealed class FragmentHost(IBufferFragment fragment) : IContent
    {
        public Size Measure(Size availableSpace, OutputCapabilities capabilities) => fragment.GetSize();

        public Rect Paint(in CellBufferView buffer, in Rect bounds, in Style style, OutputCapabilities capabilities)
        {
            buffer.AddFragment(bounds.Column, bounds.Row, fragment, style);
            var size = fragment.GetSize();
            return new Rect(bounds.Column, bounds.Row, size.Columns, size.Rows);
        }
    }

    // A minimal out-of-band fragment — never actually emits; default Clip → null (can't crop).
    private sealed class FakeFragment(int width, int height) : IBufferFragment
    {
        public Size GetSize() => new(width, height);
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
    }

    private sealed class CroppableFragment(int width, int height) : IBufferFragment
    {
        private readonly Size _size = new(width, height);
        public Size GetSize() => _size;
        public bool IsSupported(OutputCapabilities capabilities) => true;
        public void Emit(int column, int row, IBufferWriter<byte> output, OutputCapabilities capabilities) { }
        public IBufferFragment Clip(in Rect visible) => new CroppableFragment(visible.Columns, visible.Rows);
    }
}
