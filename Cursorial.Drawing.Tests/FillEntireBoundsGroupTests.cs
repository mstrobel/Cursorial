using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The compositor-level facts behind the `FillEntireBounds` background fill (task #18, settling the
/// design doc's OPEN cell-KIND question): what a styled blank of each kind lets a LOWER layer's glyph
/// do at composite. The design section ("`FillEntireBounds`: the clear becomes a background fill")
/// requires "owns the rect" to hold AT COMPOSITE for the opaque arm, and defers the cell-kind choice
/// to exactly this observation.
/// </summary>
/// <remarks>
/// The probe half (the first three tests) pins the pre-existing compositor contract the choice rests
/// on: <c>SceneCompositor.CompositeCell</c> classifies a source cell by whether it carries a grapheme.
/// A glyphless styled blank — today's clear's cell shape — takes the MERGING path: the destination
/// keeps its glyph, so a lower layer's glyph rides into the target cell (hidden as fg == bg under an
/// opaque RGB cover, but VISIBLY alive under a <see cref="Color.Default"/> cover, whose non-RGB kind
/// wins both channels outright and repaints the glyph in terminal defaults). The durable whitespace
/// cell (<see cref="Cell.DurableEmpty"/>'s NBSP) takes the glyph-bearing REPLACE path and owns the
/// rect. Hence the fill's opaque arm writes the durable kind.
/// </remarks>
public class FillEntireBoundsGroupTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);

    // ---- The cell-kind probe -----------------------------------------------------------------

    [Fact]
    public void Probe_NonDurableStyledBlank_OpaqueRgbCover_LowerGlyphRidesIntoTheTargetCell()
    {
        // Today's clear's cell shape: glyphless, styled, non-durable. FillRectangle writes exactly
        // that (a background-only cell via the raw indexer). The lower glyph must be observed in
        // the composited target cell — the merging path keeps it, hiding it as fg == bg.
        using var lower = Scene.Create(4, 1);
        lower.Draw(ctx => ctx.DrawText(0, 0, "x", new BrushedStyle { Foreground = new SolidColorBrush(Red) }));

        using var cover = Scene.Create(4, 1);
        cover.Draw(ctx => ctx.FillRectangle(new Rect(0, 0, 4, 1),
                                            new BrushedStyle { Background = new SolidColorBrush(Blue) }));

        var target = Composite(Green, 4, 1, [new SceneLayer(lower), new SceneLayer(cover)]);

        Assert.Equal("x", target[0, 0].Grapheme);            // the glyph CONTRIBUTES — it rides through
        Assert.Equal(Blue, target[0, 0].Style.Background);
        Assert.Equal(Blue, target[0, 0].Style.Foreground);   // hidden only because fg == bg
    }

    [Fact]
    public void Probe_NonDurableStyledBlank_DefaultCover_LowerGlyphStaysVisiblyAlive()
    {
        // The killer case for matching today's kind: a Color.Default background is non-RGB, so the
        // merging path's Composite returns Default for BOTH channels — the lower glyph is repainted
        // in terminal default fg over terminal default bg, i.e. visibly alive under the "cover".
        using var lower = Scene.Create(4, 1);
        lower.Draw(ctx => ctx.DrawText(0, 0, "x", new BrushedStyle { Foreground = new SolidColorBrush(Red) }));

        using var cover = Scene.Create(4, 1);
        cover.Draw(ctx => ctx.FillRectangle(new Rect(0, 0, 4, 1),
                                            new BrushedStyle { Background = new SolidColorBrush(Color.Default) }));

        var target = Composite(Green, 4, 1, [new SceneLayer(lower), new SceneLayer(cover)]);

        Assert.Equal("x", target[0, 0].Grapheme);
        Assert.Equal(Color.Default, target[0, 0].Style.Background);
        Assert.Equal(Color.Default, target[0, 0].Style.Foreground); // default-on-default: the glyph SHOWS
    }

    [Fact]
    public void Probe_DurableBlank_ReplacesTheLowerGlyph()
    {
        // The fill family's occluder: a durable (NBSP-bearing) cell takes the glyph-bearing REPLACE
        // path and owns the rect — the shape the opaque arm must converge on.
        using var lower = Scene.Create(4, 1);
        lower.Draw(ctx => ctx.DrawText(0, 0, "x", new BrushedStyle { Foreground = new SolidColorBrush(Red) }));

        using var cover = Scene.Create(4, 1);
        cover.Draw(ctx => ctx.FillOpaque(new Rect(0, 0, 4, 1),
                                         new BrushedStyle { Background = new SolidColorBrush(Blue) }));

        var target = Composite(Green, 4, 1, [new SceneLayer(lower), new SceneLayer(cover)]);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, target[0, 0].Grapheme);  // replaced — owned
        Assert.Equal(Blue, target[0, 0].Style.Background);
    }

    // ---- The fill's compositor-level contract --------------------------------------------------

    // "aaaa bbbb" is exactly the 9-column budget: one line, re-centred to row 1 of 3 by the fill's
    // vertical centring, so rows 0 and 2 and the line's right are the surround.
    private static FormattedText FillDocument(PartialStyle defaultStyle = default) =>
        new TextFormatter().Format(new RichTextBuilder(defaultStyle).Run("aaaa bbbb").Build(),
                                   9, fillEntireBounds: true);

    private static LinearGradientBrush Sweep(Color start, Color end) =>
        new(start, end, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);

    [Fact]
    public void FillWithNoResolvableBackground_ContributesNothing()
    {
        // The design section's contract test: no preference Background, no document-default
        // background ⇒ the fill SKIPS (≡ filling Transparent). The cells the document doesn't
        // paint stay the scene's transparent blank — on the member AND on a group surface — and
        // the backdrop survives the composite. Sibling of BrushedFormattedTextGroupTests.
        // CellsABrushedDocumentNeverPaints_StayTransparentThroughAGroup: same observation point,
        // with the one operation that USED to punch an opaque rectangle here turned on.
        using var member = Scene.Create(12, 3);
        member.Draw(ctx => ctx.DrawFormattedText(FillDocument(), new Rect(0, 0, 12, 3),
                                                 Sweep(Red, Blue), OutputCapabilities.None));

        using var surface = Scene.Create(12, 3);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member)]);

        // The document re-centres to row 1; (9,1) is past its line, rows 0/2 are all surround.
        foreach (var (column, row) in new[] { (0, 0), (11, 0), (9, 1), (0, 2), (11, 2) })
        {
            Assert.Equal(CellStyle.Transparent, member.GetCell(column, row).Style);
            Assert.Equal(CellStyle.Transparent, surface.GetCell(column, row).Style);
        }

        var flat = Composite(Green, 12, 3, [new SceneLayer(member)]);
        var grouped = Composite(Green, 12, 3, [new SceneLayer(surface)]);

        foreach (var (column, row) in new[] { (0, 0), (11, 0), (9, 1), (0, 2), (11, 2) })
        {
            Assert.Equal(Green, flat[column, row].Style.Background);
            Assert.Equal(Green, grouped[column, row].Style.Background);
        }
    }

    [Fact]
    public void OpaqueFill_OwnsTheRectOverALowerLayer()
    {
        // The probe's consequence at the real API: a FillEntireBounds document with a stated
        // opaque background must OWN its surround at composite — a lower layer's glyph does not
        // contribute, which is exactly what the durable cell kind buys.
        using var lower = Scene.Create(12, 3);
        lower.Draw(ctx => ctx.DrawText(0, 2, "x", new BrushedStyle { Foreground = new SolidColorBrush(Red) }));

        using var upper = Scene.Create(12, 3);
        upper.Draw(ctx => ctx.DrawFormattedText(FillDocument(PartialStyle.WithBackground(Blue)),
                                                new Rect(0, 0, 12, 3), OutputCapabilities.None));

        var target = Composite(Green, 12, 3, [new SceneLayer(lower), new SceneLayer(upper)]);

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, target[0, 2].Grapheme);   // owned — not "x"
        Assert.Equal(Blue, target[0, 2].Style.Background);
    }

    [Fact]
    public void PreferenceBackground_ReachesTheFill_AtTheDrawFormattedTextBoundary()
    {
        // Mechanic: the fill's source reads at the DrawFormattedText boundary and is THREADED to
        // Paint — the preference's Background wins over the document default's, and it never rides
        // the per-run resolver (whose decode drops Background deliberately), so the document's own
        // painted line keeps its cells.
        using var member = Scene.Create(12, 3);
        member.Draw(ctx => ctx.DrawFormattedText(FillDocument(PartialStyle.WithBackground(Green)),
                                                 new Rect(0, 0, 12, 3), OutputCapabilities.None,
                                                 new BrushedStyle { Background = new SolidColorBrush(Blue) }));

        Assert.Equal(Blue, member.GetCell(0, 0).Style.Background);      // preference wins the surround
        Assert.Equal("a", member.GetCell(0, 1).Grapheme);               // the document still paints
        Assert.Equal(Green, member.GetCell(0, 1).Style.Background);     // its cells keep the document rung
    }

    /// <summary>Composites <paramref name="layers"/> onto an opaque base of <paramref name="baseBackground"/>.</summary>
    private static CellBuffer Composite(Color baseBackground, int columns, int rows, ReadOnlySpan<SceneLayer> layers)
    {
        var baseStyle = CellStyle.Default.WithBackground(baseBackground);
        var buffer = new CellBuffer(columns, rows);
        buffer.Fill(new Cell(null, CellKind.Single, baseStyle));
        new SceneCompositor(baseStyle).Composite(layers, buffer.AsView());
        return buffer;
    }
}
