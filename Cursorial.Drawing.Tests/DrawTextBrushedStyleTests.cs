using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// <c>DrawText</c> takes a <see cref="BrushedStyle"/>: the brushes that used to travel BESIDE a
/// <see cref="CellStyle"/> now ride inside one value, the same shape
/// <see cref="Cursorial.Rendering.Fonts.IGlyphFont.Paint(in CellBufferView, int, int, ReadOnlySpan{char}, in CellStyle, in BrushedStyle, in Rect)"/>
/// already took — base style plus per-cell delta.
/// <para>
/// Every assertion reads real cells off a composited frame. The two channels the migration could
/// silently move are pinned here: the meaning of an ABSENT background (§<see cref="BrushedStyle_AbsentBackground_IsNoOpinion_SoTheBaseStylesBackgroundSurvives"/>)
/// and the RECT the brushes sample against (§<see cref="BrushedStyle_Background_SamplesTheFullMultiLineExtent_LikeTheForeground"/>).
/// </para>
/// </summary>
public class DrawTextBrushedStyleTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color Slate = Color.FromRgb(50, 60, 70);

    private static GradientStop[] BlackToWhite => [new(0.0, Black), new(1.0, Color.FromRgb(255, 255, 255))];

    private static LinearGradientBrush Ramp() => new(BlackToWhite);

    // ───────────── the capability that must survive the signature change ─────────────

    /// <summary>
    /// A gradient foreground still ramps PER COLUMN through the style overload — and lands on
    /// exactly the values the brush overload produces, which is the whole of "the sampling rect did
    /// not move" for the single-line case (see <c>DrawingContextGradientTests.DrawText_SamplesForegroundGradientPerGlyph</c>,
    /// whose numbers these are).
    /// </summary>
    [Fact]
    public void BrushedStyle_GradientForeground_RampsPerColumn()
    {
        var buffer = DrawHarness.Render(2, 1,
                                        ctx => ctx.DrawText(0, 0, "AB", new BrushedStyle { Foreground = Ramp() }));

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal("B", buffer[1, 0].Grapheme);
        Assert.Equal(64, buffer[0, 0].Style.Foreground.Red);
        Assert.Equal(191, buffer[1, 0].Style.Foreground.Red);
    }

    // ───────────── trap 1: what an ABSENT background means, at each entry point ─────────────

    /// <summary>
    /// On a TEMPLATE, <see langword="null"/> is NO OPINION — so the base style's background reaches the
    /// cell untouched. This is the one semantic the style overload deliberately does NOT inherit from
    /// the brush overload, whose <c>null</c> has always meant <see cref="Brushes.Transparent"/>.
    /// </summary>
    [Fact]
    public void BrushedStyle_AbsentBackground_IsNoOpinion_SoTheBaseStylesBackgroundSurvives()
    {
        var baseStyle = CellStyle.Default.WithBackground(Slate);

        var buffer = DrawHarness.Render(2, 1,
                                        ctx => ctx.DrawText(0, 0, "A",
                                                            new BrushedStyle { Foreground = Brushes.White },
                                                            baseStyle));

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal(Slate, buffer[0, 0].Style.Background);         // the base's own background, stated by nobody else
        Assert.Equal(Black, buffer[1, 0].Style.Background);         // ...and only where the run painted
    }

    /// <summary>
    /// The BRUSH overload keeps its historical meaning verbatim: an omitted background is
    /// <see cref="Brushes.Transparent"/>, which OVERWRITES the base style's background and lets the
    /// backdrop through. Not merely "it did not throw" — the base here carries a loud background that
    /// must NOT appear.
    /// </summary>
    [Fact]
    public void BrushOverload_OmittedBackground_StillOverwritesTheBaseWithTransparent()
    {
        var baseStyle = CellStyle.Default.WithBackground(Slate);

        var buffer = DrawHarness.Render(2, 1,
                                        ctx => ctx.DrawText(0, 0, "A", Brushes.White, background: null, baseStyle));

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal(Black, buffer[0, 0].Style.Background);         // the compositor's base, NOT Slate
        Assert.NotEqual(Slate, buffer[0, 0].Style.Background);
    }

    /// <inheritdoc cref="BrushOverload_OmittedBackground_StillOverwritesTheBaseWithTransparent"/>
    [Fact]
    public void ColorOverload_OmittedBackground_StillOverwritesTheBaseWithTransparent()
    {
        var baseStyle = CellStyle.Default.WithBackground(Slate);

        var buffer = DrawHarness.Render(2, 1,
                                        ctx => ctx.DrawText(0, 0, "A", Colors.White, background: null, baseStyle));

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal(Black, buffer[0, 0].Style.Background);
        Assert.NotEqual(Slate, buffer[0, 0].Style.Background);
    }

    /// <summary>
    /// ...and the two spellings are not interchangeable at the CELL, which is what makes trap 1 a trap
    /// rather than a naming preference. <c>CellBuffer.Set</c> rescues the grapheme underneath a
    /// whitespace write whose background is not opaque — <see cref="Color.Transparent"/> qualifies,
    /// <see cref="Color.Default"/> does not — so drawing a space with each lands a different GRAPHEME.
    /// </summary>
    [Fact]
    public void SpaceOverContent_TransparentRescuesTheGlyph_WhereNoOpinionOverwritesIt()
    {
        var transparent = DrawHarness.Render(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "A", Brushes.White);
            ctx.DrawText(0, 0, " ", Brushes.White);            // brush overload: omitted background ⇒ Transparent
        });

        var noOpinion = DrawHarness.Render(2, 1, ctx =>
        {
            ctx.DrawText(0, 0, "A", Brushes.White);
            ctx.DrawText(0, 0, " ", new BrushedStyle { Foreground = Brushes.White });   // style: absent ⇒ no opinion
        });

        Assert.Equal("A", transparent[0, 0].Grapheme);         // transparent let the glyph underneath survive
        Assert.Equal(" ", noOpinion[0, 0].Grapheme);           // Color.Default is opaque to Set, so the space landed
    }

    // ───────────── the channels a brush pair could never carry ─────────────

    /// <summary>
    /// The style's ATTRIBUTE and UNDERLINE channels reach the painted cells — the payload that had no
    /// argument to travel in when the primitive took two brushes and a whole style, and the reason a
    /// caller had to pre-fold them into <c>baseStyle</c> instead of stating them per operation.
    /// </summary>
    [Fact]
    public void BrushedStyle_AttributesAndUnderline_ReachThePaintedCells()
    {
        var brushedStyle = new BrushedStyle { Foreground = Brushes.White }
                       .Weighing(TextWeight.Bold)
                       .Applying(TextAttributes.Blink)
                       .Underlining(UnderlineStyle.Curly, Brushes.Red);

        var buffer = DrawHarness.Render(2, 1, ctx => ctx.DrawText(0, 0, "A", brushedStyle));

        var style = buffer[0, 0].Style;

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.True(style.Attributes.HasFlag(TextAttributes.Bold), $"Bold never arrived: {style.Attributes}");
        Assert.True(style.Attributes.HasFlag(TextAttributes.Blink), $"Blink never arrived: {style.Attributes}");
        Assert.True(style.Attributes.HasFlag(TextAttributes.Underline), $"Underline never arrived: {style.Attributes}");
        Assert.Equal(UnderlineStyle.Curly, style.UnderlineStyle);
        Assert.Equal(Colors.Red, style.UnderlineColor);
    }

    /// <summary>
    /// The style is a DELTA: a channel it declines to state falls through to the base style rather
    /// than being reset. The discriminator is an attribute the base carries and the style never
    /// mentions.
    /// </summary>
    [Fact]
    public void BrushedStyle_LeavesTheBaseStylesOwnAttributesAlone()
    {
        var baseStyle = CellStyle.Default.AddAttributes(TextAttributes.Italic);

        var buffer = DrawHarness.Render(2, 1,
                                        ctx => ctx.DrawText(0, 0, "A",
                                                            new BrushedStyle { Foreground = Brushes.White }
                                                                .Applying(TextAttributes.Blink),
                                                            baseStyle));

        var style = buffer[0, 0].Style;
        Assert.True(style.Attributes.HasFlag(TextAttributes.Italic), $"the base's Italic was dropped: {style.Attributes}");
        Assert.True(style.Attributes.HasFlag(TextAttributes.Blink), $"the delta's Blink never arrived: {style.Attributes}");
    }

    // ───────────── trap 2: the sampling rect, which is observable ─────────────

    /// <summary>
    /// Both brush channels sample against the SAME rect — the full multi-line extent (widest line ×
    /// line count) anchored at the start cell — which is what the brush overload has always done and
    /// what <see cref="BrushedStyle.Resolve(int, int, in Rect)"/>'s single-rect form means.
    /// <para>
    /// The discriminator is the SHORT second line: column 0 of a one-column line would sit at the
    /// ramp's midpoint if the rect were per line, and at its head if the rect is the run's extent.
    /// </para>
    /// </summary>
    [Fact]
    public void BrushedStyle_Background_SamplesTheFullMultiLineExtent_LikeTheForeground()
    {
        var buffer = DrawHarness.Render(6, 2,
                                        ctx => ctx.DrawText(0, 0, "ABCD\nX",
                                                            new BrushedStyle { Foreground = Ramp(), Background = Ramp() }));

        Assert.Equal("A", buffer[0, 0].Grapheme);
        Assert.Equal("X", buffer[0, 1].Grapheme);

        // Column 0 of BOTH rows samples the head of a 4-wide ramp — the short line did not restart it.
        Assert.Equal(buffer[0, 0].Style.Background, buffer[0, 1].Style.Background);
        Assert.Equal(buffer[0, 0].Style.Foreground, buffer[0, 1].Style.Foreground);
        Assert.Equal(32, buffer[0, 0].Style.Background.Red);       // 0.125 of the ramp, not 0.5

        // ...and non-vacuously: the ramp really does traverse the first line.
        Assert.True(buffer[3, 0].Style.Background.Red > buffer[0, 0].Style.Background.Red,
                    $"the ramp barely moved: {buffer[0, 0].Style.Background} → {buffer[3, 0].Style.Background}");

        // Foreground and background resolve against the identical rect, cell for cell.
        for (var column = 0; column < 4; column++)
            Assert.Equal(buffer[column, 0].Style.Background, buffer[column, 0].Style.Foreground);
    }
}
