using Cursorial.Drawing;
using Cursorial.Drawing.Media;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Imaging;
using Cursorial.Rendering.Media;

namespace Cursorial.Tests.Drawing;

/// <summary>
/// The <b>negative-origin sampling invariant</b>: <see cref="IBrush.ColorAt"/> reads
/// <c>bounds.Column</c>/<c>bounds.Row</c> only as a <em>subtrahend</em>, so sampling cell
/// (<c>c</c>, <c>r</c>) against a bounds whose origin is negative is identical to sampling
/// (<c>c − bounds.Column</c>, <c>r − bounds.Row</c>) against the same rectangle shifted to a zero
/// origin. Deferred stroke / braille / text records capture the ambient translate at record time, and a
/// negative translate (scrolled content) legitimately places a record's sampling rect partly above/left
/// of the scene — so brushes are handed a signed-origin rect directly and must honour this identity
/// themselves.
/// </summary>
/// <remarks>
/// This used to be guaranteed <em>by construction</em> by an internal signed carrier that shifted both
/// the sample point and the rect to a zero origin before calling <see cref="IBrush.ColorAt"/>. The
/// carrier is gone (<see cref="Rect"/> is <see cref="int"/>-backed and carries a signed origin fine), so
/// the property is pinned here instead — it must hold for every brush added later.
/// </remarks>
public class BrushNegativeOriginEquivalenceTests
{
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static readonly Color White = Color.FromRgb(255, 255, 255);
    private static readonly Color Red = Color.FromRgb(255, 0, 0);
    private static readonly Color Blue = Color.FromRgb(0, 0, 255);

    // 2×2: TL red, TR green, BL blue, BR white (all opaque).
    private static DecodedImage Img2x2() => new(2, 2,
                                                [
                                                    255, 0, 0, 255,   0, 255, 0, 255,
                                                    0, 0, 255, 255,   255, 255, 255, 255,
                                                ]);

    /// <summary>Every brush kind the framework ships, in configurations that actually vary by cell.</summary>
    private static (string Name, IBrush Brush)[] EveryBrushKind() =>
    [
        ("solid", new SolidColorBrush(Red)),
        ("solid-translucent", new SolidColorBrush(Red, 0.5)),
        ("linear-horizontal", new LinearGradientBrush(Black, White)),
        ("linear-vertical", new LinearGradientBrush([new(0.0, Black), new(1.0, White)],
                                                    RelativePoint.TopLeft, RelativePoint.BottomLeft)),
        ("linear-diagonal-repeat", new LinearGradientBrush([new(0.0, Red), new(0.5, Blue), new(1.0, White)],
                                                           RelativePoint.TopLeft, RelativePoint.BottomRight,
                                                           GradientSpread.Repeat)),
        ("linear-reflect", new LinearGradientBrush([new(0.0, Red), new(1.0, Blue)],
                                                   RelativePoint.TopLeft, RelativePoint.TopRight,
                                                   GradientSpread.Reflect)),
        ("radial", new RadialGradientBrush(White, Black)),
        ("radial-offcenter", new RadialGradientBrush([new(0.0, Red), new(1.0, Blue)],
                                                     new RelativePoint(0.25, 0.75), 0.3, 0.6)),
        ("conic", new ConicGradientBrush(Red, Blue)),
        ("conic-rotated", new ConicGradientBrush(Red, Blue, RelativePoint.Center, 37.0)),
        ("image-fill", new ImageBrush(Img2x2(), Stretch.Fill, BrushInterpolation.Bilinear)),
        ("image-none", new ImageBrush(Img2x2(), Stretch.None, BrushInterpolation.NearestNeighbor)),
        ("image-uniform", new ImageBrush(Img2x2(), Stretch.Uniform, BrushInterpolation.Bilinear)),
        ("image-uniform-to-fill", new ImageBrush(Img2x2(), Stretch.UniformToFill, BrushInterpolation.NearestNeighbor)),
        ("tile", new TileBrush(Img2x2(), new Size(2, 2))),
        ("tile-flip-xy", new TileBrush(Img2x2(), new Size(3, 2), TileMode.FlipXY)),
        ("tile-none", new TileBrush(Img2x2(), new Size(2, 2), TileMode.None)),
    ];

    // Negative, straddling, and zero origins; the sample window below extends past every edge.
    private static readonly (int Column, int Row)[] Origins =
    [
        (-1, -1), (-7, -3), (-4, 0), (0, -5), (-100, -100), (-1, 4), (3, -2), (0, 0),
    ];

    /// <summary>
    /// The core invariant, swept over every brush kind × origin × a sample window that covers the rect,
    /// its edges, and cells outside it in every direction.
    /// </summary>
    [Fact]
    public void ColorAt_NegativeOrigin_EqualsShiftedToZero()
    {
        foreach ((string name, var brush) in EveryBrushKind())
        foreach ((int originColumn, int originRow) in Origins)
        {
            var signed = new Rect(originColumn, originRow, 6, 4);
            var shifted = new Rect(0, 0, signed.Columns, signed.Rows);

            for (int row = originRow - 2; row < signed.RowEnd + 2; row++)
            for (int column = originColumn - 2; column < signed.ColumnEnd + 2; column++)
            {
                var through = brush.ColorAt(column, row, signed);
                var direct = brush.ColorAt(column - originColumn, row - originRow, shifted);

                Assert.True(through == direct,
                            $"{name}: ColorAt({column}, {row}, {signed}) = {through}, but the shifted-to-zero " +
                            $"equivalent ColorAt({column - originColumn}, {row - originRow}, {shifted}) = {direct}.");
            }
        }
    }

    /// <summary>
    /// The same invariant through <see cref="BrushedStyle.Resolve(int, int, in Rect)"/> — the
    /// whole-template path a deferred text run takes, resolving three brush channels at once.
    /// </summary>
    [Fact]
    public void TemplateResolve_NegativeOrigin_EqualsShiftedToZero()
    {
        foreach ((string name, var brush) in EveryBrushKind())
        {
            var template = new BrushedStyle { Foreground = brush, Background = brush, UnderlineColor = brush };
            var signed = new Rect(-3, -2, 5, 4);
            var shifted = new Rect(0, 0, signed.Columns, signed.Rows);

            for (int row = signed.Row - 1; row < signed.RowEnd + 1; row++)
            for (int column = signed.Column - 1; column < signed.ColumnEnd + 1; column++)
            {
                var through = template.Resolve(column, row, in signed);
                var direct = template.Resolve(column - signed.Column, row - signed.Row, in shifted);

                Assert.True(through.Foreground == direct.Foreground &&
                            through.Background == direct.Background &&
                            through.UnderlineColor == direct.UnderlineColor,
                            $"{name}: template resolve at ({column}, {row}) against {signed} differs from the " +
                            "shifted-to-zero equivalent.");
            }
        }
    }

    /// <summary>
    /// The end-to-end deferred <b>stroke</b> path: a negative translate scrolls a gradient-penned box off
    /// the top-left, so its record's sampling bounds carry a negative origin. The on-scene remainder must
    /// be colored exactly like the corresponding cells of the same box drawn un-scrolled.
    /// </summary>
    [Fact]
    public void DeferredStroke_NegativeTranslate_ColorsMatchTheUnscrolledBox()
    {
        var pen = new Pen(new LinearGradientBrush([new(0.0, Red), new(0.5, White), new(1.0, Blue)],
                                                  RelativePoint.TopLeft, RelativePoint.BottomRight));

        var reference = DrawHarness.Render(12, 9, ctx => ctx.DrawBox(new Rect(0, 0, 10, 8), pen));
        var scrolled = DrawHarness.Render(12, 9, ctx =>
        {
            using (ctx.PushTranslate(-3, -2))
                ctx.DrawBox(new Rect(0, 0, 10, 8), pen);   // sampling bounds land at scene (−3, −2, 10, 8)
        });

        // Scene (3 + i, 2 + j) in the reference is the same box cell as scene (i, j) in the scrolled render.
        int inked = 0;
        for (int j = 0; j + 2 < 9; j++)
        for (int i = 0; i + 3 < 12; i++)
        {
            var expected = reference[i + 3, j + 2];
            var actual = scrolled[i, j];
            Assert.Equal(expected.Grapheme, actual.Grapheme);
            Assert.Equal(expected.Style.Foreground, actual.Style.Foreground);
            if (!string.IsNullOrEmpty(expected.Grapheme)) inked++;
        }

        Assert.True(inked > 8, $"Expected the scrolled box to still paint many cells; only {inked} had ink.");
    }

    /// <summary>
    /// The end-to-end deferred <b>braille</b> path (a diagonal line rasterizes to braille): same scroll,
    /// same requirement.
    /// </summary>
    [Fact]
    public void DeferredBraille_NegativeTranslate_ColorsMatchTheUnscrolledLine()
    {
        var pen = new Pen(new LinearGradientBrush([new(0.0, Red), new(1.0, Blue)],
                                                  RelativePoint.TopLeft, RelativePoint.BottomRight));

        var reference = DrawHarness.Render(14, 11, ctx => ctx.DrawLine(0, 0, 11, 9, pen));
        var scrolled = DrawHarness.Render(14, 11, ctx =>
        {
            using (ctx.PushTranslate(-2, -2))
                ctx.DrawLine(0, 0, 11, 9, pen);   // sampling bounds land at scene (−2, −2, 12, 10)
        });

        int inked = 0;
        for (int j = 0; j + 2 < 11; j++)
        for (int i = 0; i + 2 < 14; i++)
        {
            var expected = reference[i + 2, j + 2];
            var actual = scrolled[i, j];
            Assert.Equal(expected.Grapheme, actual.Grapheme);
            Assert.Equal(expected.Style.Foreground, actual.Style.Foreground);
            if (!string.IsNullOrEmpty(expected.Grapheme)) inked++;
        }

        Assert.True(inked > 4, $"Expected the scrolled line to still paint braille cells; only {inked} had ink.");
    }

    /// <summary>
    /// The end-to-end deferred <b>text</b> path: <c>DrawText</c>'s brush bounds are anchored at the run's
    /// LOCAL start cell, so a negative local anchor hands the template a negative-origin rect directly.
    /// </summary>
    [Fact]
    public void DeferredText_NegativeAnchor_ColorsMatchTheZeroAnchoredRun()
    {
        var brush = new LinearGradientBrush([new(0.0, Black), new(0.5, Red), new(1.0, White)],
                                            RelativePoint.TopLeft, RelativePoint.BottomRight);

        var reference = DrawHarness.Render(6, 6, ctx => ctx.DrawText(0, 0, "abc\ndef\nghi", DrawHarness.Ink(brush)));
        var scrolled = DrawHarness.Render(6, 6, ctx =>
        {
            using (ctx.PushTranslate(2, 3))
                ctx.DrawText(-2, -3, "abc\ndef\nghi", DrawHarness.Ink(brush));   // bounds = local (−2, −3, 3, 3)
        });

        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        {
            Assert.Equal(reference[column, row].Grapheme, scrolled[column, row].Grapheme);
            Assert.Equal(reference[column, row].Style.Foreground, scrolled[column, row].Style.Foreground);
        }
    }
}
