using Cursorial.Drawing;
using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The rectangle-fill primitives take a <see cref="BrushedStyle"/>, completing what
/// <c>DrawText</c> started: the single brush and the flag WORD that used to travel side by side now
/// ride one value, and the channels neither could carry — a foreground, an underline, a hyperlink, a
/// blending mode — arrive with them.
/// <para>
/// Most assertions read the RAW SCENE CELL rather than a composited frame. What a fill wrote and what
/// survives compositing are different questions, and the three families differ precisely in the cell
/// they write: <c>FillRectangle</c> writes background-only cells (no glyph, so a lower layer's shows
/// through), <c>FillOpaque</c> writes space-bearing cells that OCCLUDE, and <c>PaintRectangle</c>
/// writes background-only cells through <see cref="CellBuffer.Set"/> so the blend and the whitespace
/// rescue happen INSIDE the scene. The composited assertions are reserved for the claims that are
/// only true across layers.
/// </para>
/// </summary>
public class FillBrushedStyleTests
{
    private static readonly Color Teal = Color.FromRgb(0, 128, 128);
    private static readonly Color Slate = Color.FromRgb(50, 60, 70);
    private static readonly Color Frosted = Color.FromRgba(0, 128, 128, 128);

    /// <summary>A horizontal black→white ramp: the sampled red channel is a direct readout of the
    /// column's position WITHIN the rect it was sampled against, which is what makes the bounds
    /// question answerable from a cell.</summary>
    private static LinearGradientBrush Ramp() => new(Colors.TrueBlack, Colors.TrueWhite);

    private static SolidColorBrush Solid(Color color) => new(color);

    /// <summary>The cells the primitive WROTE — no compositor, no base layer, no passthrough rules.</summary>
    private static Scene Painted(int columns, int rows, Action<DrawingContext> draw)
    {
        var scene = Scene.Create(columns, rows);
        scene.Draw(draw);
        return scene;
    }

    // ───────────── trap 2: the three families are not interchangeable ─────────────

    /// <summary>
    /// <c>FillRectangle</c> through a template still writes a BACKGROUND-ONLY cell — no grapheme — so
    /// the scene stays glyph-transparent and a lower layer shows through on composite.
    /// </summary>
    [Fact]
    public void FillRectangle_BrushedStyle_StillWritesBackgroundOnlyCells()
    {
        using var scene = Painted(2, 1, ctx => ctx.FillRectangle(new Rect(0, 0, 2, 1),
                                                                 new BrushedStyle { Background = Solid(Teal) }));

        Assert.Null(scene.GetCell(0, 0).Grapheme);
        Assert.Null(scene.GetCell(1, 0).Grapheme);
        Assert.Equal(Teal, scene.GetCell(0, 0).Style.Background);
        Assert.Equal(Teal, scene.GetCell(1, 0).Style.Background);
    }

    /// <summary>
    /// <c>FillOpaque</c> through a template still writes SPACE-BEARING cells — the durable empty
    /// grapheme — which is the whole of its occluding contract.
    /// </summary>
    [Fact]
    public void FillOpaque_BrushedStyle_StillWritesSpaceBearingCells()
    {
        using var scene = Painted(2, 1, ctx => ctx.FillOpaque(new Rect(0, 0, 2, 1),
                                                              new BrushedStyle { Background = Solid(Teal) }));

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, scene.GetCell(0, 0).Grapheme);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, scene.GetCell(1, 0).Grapheme);
        Assert.Equal(Teal, scene.GetCell(0, 0).Style.Background);
    }

    /// <summary>
    /// <c>PaintRectangle</c> is the third thing: background-only like <c>FillRectangle</c>, but routed
    /// through <see cref="CellBuffer.Set"/> so it blends and rescues INSIDE the scene. The
    /// discriminator is a same-scene glyph — <c>FillRectangle</c> obliterates it (a raw write it has no
    /// <c>overwrite</c> switch to decline), <c>PaintRectangle</c> leaves it standing at its default.
    /// </summary>
    [Fact]
    public void PaintRectangle_BrushedStyle_LeavesASameSceneGlyphStanding_WhereFillRectangleObliteratesIt()
    {
        var tint = new BrushedStyle { Background = Brushes.Transparent };

        using var painted = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.PaintRectangle(new Rect(0, 0, 2, 1), tint);
        });

        using var filled = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillRectangle(new Rect(0, 0, 2, 1), tint);
        });

        Assert.Equal("X", painted.GetCell(0, 0).Grapheme);   // Set's whitespace rescue, intra-scene
        Assert.Null(filled.GetCell(0, 0).Grapheme);          // the raw write, unconditional
    }

    /// <summary>
    /// The family distinction that only exists ACROSS layers: a background-only fill lets the lower
    /// scene's glyph through, a space-bearing one hides it. Same template, same translucent colour —
    /// only the primitive differs.
    /// </summary>
    [Fact]
    public void BrushedStyle_FillRectangleLetsALowerGlyphThrough_WhereFillOpaqueOccludesIt()
    {
        var tint = new BrushedStyle { Background = Solid(Frosted) };

        var through = DrawHarness.RenderLayers(2, 1, null,
                                               lower => lower.DrawText(0, 0, "X", Brushes.White),
                                               upper => upper.FillRectangle(new Rect(0, 0, 2, 1), tint));

        var hidden = DrawHarness.RenderLayers(2, 1, null,
                                              lower => lower.DrawText(0, 0, "X", Brushes.White),
                                              upper => upper.FillOpaque(new Rect(0, 0, 2, 1), tint));

        Assert.Equal("X", through[0, 0].Grapheme);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, hidden[0, 0].Grapheme);
    }

    // ───────────── trap 1: the attribute WORD carries axis-owning flags ─────────────

    /// <summary>
    /// <b>THE trap.</b> <see cref="PartialStyle.WithApplied"/> — and therefore
    /// <see cref="BrushedStyle.Applying"/> — REJECTS <see cref="TextAttributes.Bold"/>,
    /// <c>Faint</c>, <c>Italic</c> and <c>Underline</c>: they have their own axes. And this exact word
    /// is what <c>TextPresenter</c>'s band fill hands the primitive
    /// (<c>baseStyle.Attributes &amp; FillAttributes | Inverse</c>, where the allowlist includes Bold,
    /// Faint and Italic), so a wrapper that folds the word with a single <c>Applying</c> throws on a
    /// real path. The fold must decompose per axis, the way
    /// <c>DrawingContext.CreateBrushResolver</c>'s base-attribute leg does.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Word_CarryingAxisOwningFlags_FoldsPerAxis_RatherThanThrowing(int family)
    {
        var word = TextAttributes.Bold | TextAttributes.Italic | TextAttributes.Inverse;
        var region = new Rect(0, 0, 2, 1);

        using var scene = Painted(2, 1, ctx =>
        {
            switch (family)
            {
                case 0: ctx.FillRectangle(region, Teal, word); break;
                case 1: ctx.FillOpaque(region, Teal, word); break;
                default: ctx.PaintRectangle(region, Teal, word); break;
            }
        });

        Assert.Equal(word, scene.GetCell(0, 0).Style.Attributes);
        Assert.Equal(Teal, scene.GetCell(0, 0).Style.Background);
    }

    /// <summary>
    /// A word carrying BOTH weights is malformed — Bold and Faint share the SGR 22 reset, so the pair
    /// is a state the encoder cannot spell. Folding per axis IMPOSES one rather than unioning into it,
    /// and Bold wins: the choice is arbitrary, being fixed is not.
    /// </summary>
    [Fact]
    public void Word_CarryingBothWeights_ImposesBold_RatherThanTheUnrenderablePair()
    {
        using var scene = Painted(1, 1,
                                  ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1), Teal,
                                                        TextAttributes.Bold | TextAttributes.Faint));

        var attributes = scene.GetCell(0, 0).Style.Attributes;
        Assert.True(attributes.HasFlag(TextAttributes.Bold), $"Bold was not imposed: {attributes}");
        Assert.False(attributes.HasFlag(TextAttributes.Faint), $"the unrenderable pair survived: {attributes}");
    }

    /// <summary>
    /// <see cref="TextAttributes.Underline"/> has its own axis too, and it is the one whose flag
    /// travels with a SHAPE. A word that carries it still underlines every filled cell, at the shape a
    /// fill has always produced.
    /// </summary>
    [Fact]
    public void Word_CarryingUnderline_StillUnderlinesEveryFilledCell()
    {
        using var scene = Painted(1, 1,
                                  ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1), Teal, TextAttributes.Underline));

        var style = scene.GetCell(0, 0).Style;
        Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"Underline never arrived: {style.Attributes}");
        Assert.Equal(UnderlineStyle.Single, style.UnderlineStyle);
    }

    /// <summary>
    /// ...and the same axis flags reach the cells when they arrive on a TEMPLATE, stated through the
    /// axis-typed members rather than as a flag word.
    /// </summary>
    [Fact]
    public void BrushedStyle_CarryingAxisFlags_ReachesTheFilledCells()
    {
        var template = new BrushedStyle { Background = Solid(Teal) }
                       .Weighing(TextWeight.Bold)
                       .Posturing(TextStyle.Italic)
                       .Applying(TextAttributes.Inverse);

        using var scene = Painted(1, 1, ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1), template));

        var attributes = scene.GetCell(0, 0).Style.Attributes;
        Assert.Equal(TextAttributes.Bold | TextAttributes.Italic | TextAttributes.Inverse, attributes);
    }

    /// <summary>
    /// The primitive keeps painting exactly what it is HANDED. The attribute allowlist that decides
    /// which attributes may spread onto un-inked cells belongs to the policy (<c>TextPresenter</c>'s
    /// <c>FillAttributes</c>), never here — an author who passes <see cref="TextAttributes.Blink"/>
    /// asked for it, and masking here would remove the escape hatch that ruling depends on.
    /// </summary>
    [Fact]
    public void Fill_MasksNothing_TheAllowlistBelongsToThePolicy()
    {
        using var scene = Painted(1, 1,
                                  ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1), Teal,
                                                        TextAttributes.Blink | TextAttributes.Strikethrough));

        var attributes = scene.GetCell(0, 0).Style.Attributes;
        Assert.True(attributes.HasFlag(TextAttributes.Blink), $"Blink was masked away: {attributes}");
        Assert.True(attributes.HasFlag(TextAttributes.Strikethrough), $"Strikethrough was masked away: {attributes}");
    }

    // ───────────── the channels a colour and a flag word could never carry ─────────────

    /// <summary>
    /// A template states channels the single brush had nowhere to put. The FOREGROUND is the
    /// discriminating one for <c>FillOpaque</c>: its cells bear a space, so they carry a foreground the
    /// compositor honours — a fill could not colour it before at all.
    /// </summary>
    [Fact]
    public void BrushedStyle_ForegroundUnderlineAndHyperlink_ReachTheFilledCells()
    {
        var link = new Hyperlink("https://example.invalid/");
        var template = new BrushedStyle
                       {
                           Background = Solid(Teal),
                           Foreground = Solid(Slate),
                           Hyperlink = link,
                       }.Underlining(UnderlineStyle.Curly, Brushes.Red);

        using var scene = Painted(1, 1, ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1), template));

        var style = scene.GetCell(0, 0).Style;
        Assert.Equal(Teal, style.Background);
        Assert.Equal(Slate, style.Foreground);
        Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle);
        Assert.Equal(Colors.Red, style.UnderlineColor);
        Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"the shape did not imply the flag: {style.Attributes}");
        Assert.Equal(link, style.Hyperlink);
    }

    // ───────────── trap 3: the overwrite defaults, and the rescue they guard ─────────────

    /// <summary>
    /// <c>DrawingContext.FillOpaque</c> defaults to <c>overwrite: true</c> — the raw write, which
    /// obliterates a same-scene glyph. Preserved verbatim through the template overload.
    /// </summary>
    [Fact]
    public void FillOpaque_BrushedStyle_DefaultsToOverwriting_AtTheDrawingContext()
    {
        using var scene = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1), new BrushedStyle { Background = Brushes.Transparent });
        });

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, scene.GetCell(0, 0).Grapheme);
    }

    /// <summary>
    /// ...and <c>overwrite: false</c> still rescues the glyph underneath, carrying the fill's own
    /// attributes onto the cell. This is <c>TextPresenter</c>'s band-fill shape exactly: a transparent
    /// background so <see cref="CellBuffer.Set"/> restores the FIGlet ink, and the attributes riding on
    /// top of it.
    /// </summary>
    [Fact]
    public void FillOpaque_BrushedStyle_OverwriteFalse_StillRescuesTheGlyphUnderneath()
    {
        using var scene = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1),
                           new BrushedStyle { Background = Brushes.Transparent }
                               .Applying(TextAttributes.Inverse),
                           overwrite: false);
        });

        Assert.Equal("X", scene.GetCell(0, 0).Grapheme);
        Assert.True(scene.GetCell(0, 0).Style.Attributes.HasFlag(TextAttributes.Inverse),
                    $"the fill's attribute never arrived: {scene.GetCell(0, 0).Style.Attributes}");
    }

    /// <summary>
    /// An ABSENT background on a fill template is <see cref="Color.Default"/>, not
    /// <see cref="Color.Transparent"/> — because the ground a fill resolves against is
    /// <see cref="CellStyle.Default"/>, whose background already IS <c>Default</c>. The distinction is
    /// not nominal: <c>Default</c> is OPAQUE to <see cref="CellBuffer.Set"/> and <c>Transparent</c> is
    /// not, so the two land different GRAPHEMES on the rescue path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which makes the fill template's <c>null</c> agree with <c>FillOpaque(…, Color.Default, …)</c>
    /// rather than with <c>DrawText</c>'s brush overload, whose omitted background is
    /// <see cref="Brushes.Transparent"/>.
    /// </para>
    /// <para>
    /// The stored colour is read off the OVERWRITING path, because <c>Set</c> does not store a
    /// <c>Default</c> source background verbatim — <see cref="CellStyle.BlendOver"/> reads it as "defer
    /// to the backdrop" and keeps the destination's. That is pre-existing and identical for the colour
    /// overload; the claim here is about what the template resolves TO, so it is asserted where nothing
    /// else can rewrite it.
    /// </para>
    /// </remarks>
    [Fact]
    public void BrushedStyle_AbsentBackground_IsColorDefault_NotTransparent()
    {
        using var stored = Painted(1, 1,
                                   ctx => ctx.FillOpaque(new Rect(0, 0, 1, 1),
                                                         new BrushedStyle().Applying(TextAttributes.Inverse)));

        Assert.True(stored.GetCell(0, 0).Style.Background.IsDefault,
                    $"an absent background resolved to {stored.GetCell(0, 0).Style.Background}");

        using var absent = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1), new BrushedStyle().Applying(TextAttributes.Inverse),
                           overwrite: false);
        });

        using var transparent = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.FillOpaque(new Rect(0, 0, 2, 1),
                           new BrushedStyle { Background = Brushes.Transparent }.Applying(TextAttributes.Inverse),
                           overwrite: false);
        });

        Assert.Equal(CellBuffer.DurableEmptyGrapheme, absent.GetCell(0, 0).Grapheme);   // opaque: no rescue
        Assert.Equal("X", transparent.GetCell(0, 0).Grapheme);                          // transparent: rescued
    }

    /// <summary>
    /// <c>PaintRectangle</c>'s default is the opposite one — <c>overwrite: false</c> — and it too is
    /// preserved through the template overload.
    /// </summary>
    [Fact]
    public void PaintRectangle_BrushedStyle_DefaultsToNotOverwriting()
    {
        var tint = new BrushedStyle { Background = Brushes.Transparent };

        using var byDefault = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.PaintRectangle(new Rect(0, 0, 2, 1), tint);
        });

        using var overwriting = Painted(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "X", Brushes.White);
            ctx.PaintRectangle(new Rect(0, 0, 2, 1), tint, overwrite: true);
        });

        Assert.Equal("X", byDefault.GetCell(0, 0).Grapheme);
        Assert.Null(overwriting.GetCell(0, 0).Grapheme);
    }

    // ───────────── trap 5: which rect the brushes sample against ─────────────

    /// <summary>
    /// A gradient still ramps across the filled region when no separate bounds are stated — the region
    /// is its own sampling rect, cell by cell.
    /// </summary>
    [Fact]
    public void BrushedStyle_GradientBackground_RampsAcrossTheFilledRegion()
    {
        using var scene = Painted(4, 1, ctx => ctx.FillRectangle(new Rect(0, 0, 4, 1),
                                                                 new BrushedStyle { Background = Ramp() }));

        var reds = Enumerable.Range(0, 4).Select(c => (int) scene.GetCell(c, 0).Style.Background.Red).ToArray();

        Assert.Equal([32, 96, 159, 223], reds);   // (c + 0.5) / 4 of a black→white ramp
    }

    /// <summary>
    /// The <c>brushBounds</c> overload still samples against the STATED rect rather than the painted
    /// region — the area-fill chart contract, which paints one column at a time yet wants one gradient
    /// across the whole chart.
    /// </summary>
    [Fact]
    public void BrushedStyle_BrushBounds_StillSampleAgainstTheStatedRect()
    {
        using var scene = Painted(4, 1, ctx => ctx.FillRectangle(new Rect(0, 0, 2, 1),
                                                                 new BrushedStyle { Background = Ramp() },
                                                                 new Rect(0, 0, 4, 1)));

        // The head of a FOUR-wide ramp, not of a two-wide one (which would read 64 / 191).
        Assert.Equal(32, scene.GetCell(0, 0).Style.Background.Red);
        Assert.Equal(96, scene.GetCell(1, 0).Style.Background.Red);
    }

    /// <summary>
    /// ...and it governs EVERY brush channel, not just the background — the single-rect
    /// <see cref="BrushedStyle.Resolve(int, int, in Rect)"/> form, not the four-argument one. A
    /// fill's cells are uniform: its background covers precisely the cells its (space) ink does, so
    /// there is one extent, and <c>brushBounds</c> reframes the whole style rather than the background
    /// alone. The four-argument form's split belongs to operations whose fill box is larger than their
    /// ink; a rectangle fill is not one.
    /// </summary>
    /// <remarks>
    /// The discriminator is the foreground: under the four-argument form it would sample the two-column
    /// REGION (0.25 / 0.75 → 64 / 191) while the background sampled the four-column bounds, and the two
    /// channels would disagree on a cell whose colours the caller stated with one brush.
    /// </remarks>
    [Fact]
    public void BrushedStyle_BrushBounds_GovernEveryChannel_NotJustTheBackground()
    {
        using var scene = Painted(4, 1, ctx => ctx.FillOpaque(new Rect(0, 0, 2, 1),
                                                              new BrushedStyle { Background = Ramp(), Foreground = Ramp() },
                                                              new Rect(0, 0, 4, 1)));

        Assert.Equal(32, scene.GetCell(0, 0).Style.Background.Red);
        Assert.Equal(32, scene.GetCell(0, 0).Style.Foreground.Red);
        Assert.Equal(96, scene.GetCell(1, 0).Style.Background.Red);
        Assert.Equal(96, scene.GetCell(1, 0).Style.Foreground.Red);
    }

    // ───────────── the wrappers, unchanged ─────────────

    /// <summary>
    /// The <see cref="Color"/> and <see cref="IBrush"/> overloads stay as thin wrappers — roughly sixty
    /// call sites should not be pushed through template construction to say what two arguments already
    /// say — and they land the cell they always landed.
    /// </summary>
    [Fact]
    public void ColorAndBrushOverloads_LandTheCellsTheyAlwaysLanded()
    {
        using var scene = Painted(4, 1, ctx =>
        {
            ctx.FillRectangle(new Rect(0, 0, 1, 1), Teal);
            ctx.FillRectangle(new Rect(1, 0, 1, 1), Solid(Teal));
            ctx.FillOpaque(new Rect(2, 0, 1, 1), Teal);
            ctx.PaintRectangle(new Rect(3, 0, 1, 1), Solid(Teal));
        });

        Assert.Null(scene.GetCell(0, 0).Grapheme);
        Assert.Null(scene.GetCell(1, 0).Grapheme);
        Assert.Equal(CellBuffer.DurableEmptyGrapheme, scene.GetCell(2, 0).Grapheme);
        Assert.Null(scene.GetCell(3, 0).Grapheme);

        for (var column = 0; column < 4; column++)
        {
            Assert.Equal(Teal, scene.GetCell(column, 0).Style.Background);
            Assert.Equal(default, scene.GetCell(column, 0).Style.Attributes);
            Assert.True(scene.GetCell(column, 0).Style.Foreground.IsDefault);
        }
    }

    /// <summary>
    /// The transformed path (an active translate/clip) resolves the template in LOCAL coordinates and
    /// writes at the translated scene cell, exactly as the single brush was sampled before.
    /// </summary>
    [Fact]
    public void BrushedStyle_UnderATranslate_SamplesLocallyAndWritesTranslated()
    {
        using var scene = Painted(6, 1, ctx =>
        {
            using (ctx.PushTranslate(2, 0))
                ctx.FillRectangle(new Rect(0, 0, 4, 1), new BrushedStyle { Background = Ramp() });
        });

        // A scene's unpainted blank is TRANSPARENT (it contributes nothing when composited onwards),
        // so "untouched" reads as transparent rather than as the terminal default.
        Assert.True(scene.GetCell(0, 0).Style.Background.IsTransparent);   // outside the translated region
        Assert.True(scene.GetCell(1, 0).Style.Background.IsTransparent);
        Assert.Equal(32, scene.GetCell(2, 0).Style.Background.Red);    // local column 0 of a 4-wide ramp
        Assert.Equal(223, scene.GetCell(5, 0).Style.Background.Red);   // local column 3
    }
}
