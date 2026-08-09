using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// Brushed <c>DrawFormattedText</c> rastered into a <see cref="Scene"/> and composited through an
/// <b>opacity group</b> — the surface the characterisation corpus structurally cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// The corpus (<c>Characterisation/TextCorpus.cs</c>) paints straight into a bare
/// <c>new CellBuffer(columns, rows, capabilities)</c>, so its blank is whatever
/// <c>CellBuffer.DeriveDefaultStyle</c> makes of those capabilities — for that harness, the reported terminal
/// defaults: an OPAQUE <c>#cdd6f4</c> on <c>#1e1e2e</c>, not <see cref="CellStyle.Default"/>. A scene's blank
/// is <see cref="CellStyle.Transparent"/>
/// (<c>Scene.CreateBuffer</c>), and an intermediate group surface keeps a glyph-bearing source's foreground
/// <b>verbatim</b> instead of folding it over the merged background (<c>SceneCompositor.ForIntermediate</c>) —
/// so a colour the text pipeline produces travels through two more transforms before anyone sees it, and
/// neither is observable from a corpus dump. These three tests are the observation point.
/// </para>
/// <para>
/// They are behavioural assertions, not baselines: they say what the composited result must BE, so a carrier
/// migration that changes what a run's foreground carries (a resolved <see cref="CellStyle"/> today, a
/// <see cref="BrushedStyle"/> plus a sampling frame later) fails here with a colour rather than passing
/// because the harness could not see the path. The equivalence claim itself — that routing one layer through
/// a group reproduces compositing it directly — is <c>IntermediateSurfaceEquivalenceTests</c>'s; what is new
/// here is that the layer's content comes out of the TEXT pipeline, brushed, so the alpha the group must
/// preserve is one the brush produced per cell.
/// </para>
/// </remarks>
public class BrushedFormattedTextGroupTests
{
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);
    private static readonly Color Green = Color.FromRgb(0, 255, 0);

    private static readonly Color RedHalf = Color.FromRgba(255, 0, 0, 128);
    private static readonly Color BlueHalf = Color.FromRgba(0, 0, 255, 128);

    // "aaaa bbbb" is exactly the 9-column budget, so it lays out as one line on row 0 and leaves the rest of
    // every scene below untouched — which is what the transparent-blank test needs to look at.
    private static FormattedText Document() =>
        new TextFormatter().Format(new RichTextBuilder().Run("aaaa bbbb").Build(), 9);

    private static LinearGradientBrush Sweep(Color start, Color end) =>
        new(start, end, startPoint: RelativePoint.Left, endPoint: RelativePoint.Right);

    [Fact]
    public void BrushedText_ThroughAnIdentityGroup_IsBitIdenticalToTheFlatPath()
    {
        // The floor. A gradient-coloured document is one scene layer like any other, so routing it through a
        // group surface at full opacity must reproduce the flat stack cell for cell — grapheme, kind, and
        // every style channel. If the text pipeline ever emitted a cell shape the surface has to reclassify
        // (a glyphless cell that must nevertheless replace, say), this is where it would surface, and it
        // would surface as a colour or a lost glyph rather than as a moved baseline.
        using var member = Scene.Create(12, 3);
        member.Draw(ctx => ctx.DrawFormattedText(Document(), new Rect(0, 0, 12, 3),
                                                 Sweep(Red, Blue), OutputCapabilities.None));

        var flat = Composite(Green, 12, 3, [new SceneLayer(member)]);

        using var surface = Scene.Create(12, 3);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member)]);
        var grouped = Composite(Green, 12, 3, [new SceneLayer(surface)]);

        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 12; column++)
            Assert.Equal(flat[column, row], grouped[column, row]);

        // Guard against the comparison passing on an empty raster: the sweep must actually have run.
        Assert.Equal("a", flat[0, 0].Grapheme);
        Assert.Equal("b", flat[8, 0].Grapheme);
        Assert.True(flat[0, 0].Style.Foreground.Red > flat[0, 0].Style.Foreground.Blue);
        Assert.True(flat[8, 0].Style.Foreground.Blue > flat[8, 0].Style.Foreground.Red);
    }

    [Fact]
    public void TranslucentBrushedText_KeepsItsPerCellForegroundVerbatimOnAGroupSurface()
    {
        // The rule the corpus cannot state. A brush may sample a TRANSLUCENT colour, and a per-cell brush
        // samples a different one in every column — so the group surface has to carry a whole run's worth of
        // distinct alpha-bearing foregrounds through unfolded, and the fold has to happen exactly once, at
        // the final composite where the real backdrop is known. Folding at the intermediate applies the
        // backdrop twice and the error is a colour shift, never a missing glyph.
        var brush = Sweep(RedHalf, BlueHalf);

        using var member = Scene.Create(12, 3);
        member.Draw(ctx => ctx.DrawFormattedText(Document(), new Rect(0, 0, 12, 3), brush, OutputCapabilities.None));

        using var surface = Scene.Create(12, 3);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member)]);

        // Every painted column arrives on the surface exactly as the member drew it — alpha intact, and still
        // varying column to column rather than collapsed to one sampled colour.
        for (int column = 0; column < 9; column++)
        {
            Assert.Equal(member.GetCell(column, 0).Style.Foreground, surface.GetCell(column, 0).Style.Foreground);
            Assert.Equal((byte) 128, surface.GetCell(column, 0).Style.Foreground.Alpha);
        }

        Assert.NotEqual(surface.GetCell(0, 0).Style.Foreground, surface.GetCell(8, 0).Style.Foreground);

        // And the group blends down once: the same as compositing the member directly at the same opacity.
        var flat = Composite(Green, 12, 3, [new SceneLayer(member, new CompositeParameters(opacity: 128))]);
        var grouped = Composite(Green, 12, 3, [new SceneLayer(surface, new CompositeParameters(opacity: 128))]);

        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 12; column++)
            Assert.Equal(flat[column, row], grouped[column, row]);
    }

    [Fact]
    public void CellsABrushedDocumentNeverPaints_StayTransparentThroughAGroup()
    {
        // A scene's blank IS CellStyle.Transparent, and a document paints only its own extent — so the cells
        // outside it must contribute nothing, both on the member and on the group surface reset-to-base runs
        // over. This is the property the corpus inverts by construction: its buffer blanks to the style
        // CellBuffer.DeriveDefaultStyle builds from the harness's capabilities — an opaque #1e1e2e
        // background, not CellStyle.Default — so an over-eager paint (a brush that decided it owned the
        // whole bounds, a FillEntireBounds-style clear leaking in) would be invisible there and would
        // punch an opaque rectangle into every real surface.
        using var member = Scene.Create(12, 3);
        member.Draw(ctx => ctx.DrawFormattedText(Document(), new Rect(0, 0, 12, 3),
                                                 Sweep(Red, Blue), OutputCapabilities.None));

        using var surface = Scene.Create(12, 3);
        surface.CompositeInto(SceneCompositor.ForIntermediate(), [new SceneLayer(member)]);

        // (9,0) is past the end of the only line; rows 1-2 are past the end of the document.
        foreach (var (column, row) in new[] { (9, 0), (11, 0), (0, 1), (11, 2) })
        {
            Assert.Equal(CellStyle.Transparent, member.GetCell(column, row).Style);
            Assert.Equal(CellStyle.Transparent, surface.GetCell(column, row).Style);
        }

        // Which means the backdrop survives underneath, on both paths.
        var flat = Composite(Green, 12, 3, [new SceneLayer(member)]);
        var grouped = Composite(Green, 12, 3, [new SceneLayer(surface)]);

        foreach (var (column, row) in new[] { (9, 0), (11, 0), (0, 1), (11, 2) })
        {
            Assert.Equal(Green, flat[column, row].Style.Background);
            Assert.Equal(Green, grouped[column, row].Style.Background);
        }
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
