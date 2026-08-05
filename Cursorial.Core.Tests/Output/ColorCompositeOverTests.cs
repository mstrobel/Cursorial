using Cursorial.Output;

namespace Cursorial.Tests.Output;

public class ColorCompositeOverTests
{
    private static readonly IBlendingMode[] AllModes =
    [
        BlendingModes.SourceOver, BlendingModes.Multiply, BlendingModes.Screen,
        BlendingModes.Overlay, BlendingModes.Darken, BlendingModes.Lighten, BlendingModes.Plus
    ];

    // Boundaries plus the values on either side of the 127/128 rounding seam.
    private static readonly byte[] Channels = [0, 1, 63, 64, 127, 128, 192, 254, 255];

    private static void AssertRgba(Color c, byte r, byte g, byte b, byte a)
    {
        Assert.Equal(ColorKind.Rgb, c.Kind);
        Assert.Equal(r, c.Red);
        Assert.Equal(g, c.Green);
        Assert.Equal(b, c.Blue);
        Assert.Equal(a, c.Alpha);
    }

    // ---- The reduction identity ----

    /// <summary>
    /// The license for every downstream use: nothing that composites onto an already-opaque destination
    /// can change a pixel by switching to <see cref="Color.CompositeOver"/>. This sweeps the observable
    /// contract; the algebra behind it is that at an opaque backdrop the general path's backdrop weight
    /// collapses to exactly <c>255 - source.Alpha</c> and its output alpha to exactly 255, leaving
    /// <see cref="Color.Composite"/>'s channel expression — which is what lets the implementation take
    /// the cheaper route of delegating outright.
    /// </summary>
    [Fact]
    public void OpaqueBackdrop_IsBitIdenticalToComposite()
    {
        foreach (var mode in AllModes)
        foreach (var sc in Channels)
        foreach (var bc in Channels)
        {
            // Greyscale pairs cover every (source channel, backdrop channel) combination; the rotated
            // pair additionally catches any channel-crossing mistake.
            Color[] sources = [Color.FromRgb(sc, sc, sc), Color.FromRgb(sc, (byte) (255 - sc), (byte) (sc / 2))];
            Color[] backdrops = [Color.FromRgb(bc, bc, bc), Color.FromRgb(bc, (byte) (255 - bc), (byte) (bc / 3))];

            for (var shape = 0; shape < 2; shape++)
            for (var alpha = 0; alpha <= 255; alpha++)
            {
                var source = sources[shape].WithAlpha((byte) alpha);
                var backdrop = backdrops[shape];

                var expected = Color.Composite(source, backdrop, mode);
                var actual = Color.CompositeOver(source, backdrop, mode);

                Assert.True(
                    expected == actual,
                    $"CompositeOver({source}, {backdrop}) = {actual}, expected {expected} (mode {mode.GetType().Name})");
            }
        }
    }

    // ---- Alpha preservation ----

    [Fact]
    public void TwoTranslucentOperands_PreserveAlpha()
    {
        // ib = round(128 * 127 / 255) = 64, so ao = 192; R = (255*128 + 0*64)/192 = 170,
        // B = (0*128 + 255*64)/192 = 85. Composite would report this same pixel as opaque.
        var result = Color.CompositeOver(
            Color.FromRgba(255, 0, 0, 128),
            Color.FromRgba(0, 0, 255, 128),
            BlendingModes.Default);

        AssertRgba(result, 170, 0, 85, 192);
        Assert.Equal(255, Color.Composite(Color.FromRgba(255, 0, 0, 128), Color.FromRgba(0, 0, 255, 128), BlendingModes.Default).Alpha);
    }

    [Fact]
    public void TranslucentOperands_RunTheBlendingModeBeforeCompositing()
    {
        // Multiply runs first, giving rgb(128, 128, 0); only then does the source alpha weigh that
        // against the raw backdrop. ib = round(64*127/255) = 32, ao = 160, so
        // G = (128*128 + 255*32)/160 = 153. Compositing before blending would give a different green.
        var result = Color.CompositeOver(
            Color.FromRgba(255, 128, 0, 128),
            Color.FromRgba(128, 255, 64, 64),
            BlendingModes.Multiply);

        AssertRgba(result, 128, 153, 12, 160);
    }

    /// <summary>
    /// Guards the translucent-over-translucent path against a structural mistake — a swapped operand, a
    /// dropped weight, a wrong divisor — that the hand-computed vectors above would only catch if they
    /// happened to sit on it. The reference is real-valued Porter-Duff "over", derived independently of
    /// the integer implementation. Measured worst deviation over this grid is 2.83 on a channel and 0.50
    /// on alpha; both are pure quantization, and both peak at single-digit alphas where un-premultiplying
    /// magnifies a half-unit alpha error.
    /// </summary>
    [Fact]
    public void TranslucentOverTranslucent_TracksExactPorterDuffOver()
    {
        byte[] alphas = [1, 2, 17, 64, 85, 128, 170, 200, 254];

        var worstChannel = 0.0;
        var worstAlpha = 0.0;

        foreach (var sc in Channels)
        foreach (var bc in Channels)
        foreach (var sa in alphas)
        foreach (var ba in alphas)
        {
            var source = Color.FromRgba(sc, (byte) (255 - sc), (byte) (sc / 2), sa);
            var backdrop = Color.FromRgba(bc, (byte) (255 - bc), (byte) (bc / 3), ba);

            var actual = Color.CompositeOver(source, backdrop, BlendingModes.SourceOver);

            double s = sa / 255.0, b = ba / 255.0;
            var alpha = s + b * (1 - s);

            worstChannel = Math.Max(worstChannel, Math.Abs(actual.Red - (source.Red * s + backdrop.Red * b * (1 - s)) / alpha));
            worstChannel = Math.Max(worstChannel, Math.Abs(actual.Green - (source.Green * s + backdrop.Green * b * (1 - s)) / alpha));
            worstChannel = Math.Max(worstChannel, Math.Abs(actual.Blue - (source.Blue * s + backdrop.Blue * b * (1 - s)) / alpha));
            worstAlpha = Math.Max(worstAlpha, Math.Abs(actual.Alpha - alpha * 255));
        }

        Assert.True(worstChannel <= 3.0, $"channel drifted {worstChannel:F3} from exact");
        Assert.True(worstAlpha <= 0.5, $"alpha drifted {worstAlpha:F3} from exact");
    }

    /// <summary>
    /// Un-premultiplying divides by the output alpha, which overflows a byte unless the backdrop's
    /// weight is rounded once and reused by both the numerator and the output alpha. White over white
    /// is the sharpest probe: it must stay white at every alpha pair, never wrap.
    /// </summary>
    [Fact]
    public void WhiteOverWhite_StaysWhiteAtEveryAlphaPair()
    {
        var white = Color.FromRgb(255, 255, 255);
        var black = Color.FromRgb(0, 0, 0);

        for (var sourceAlpha = 1; sourceAlpha <= 255; sourceAlpha++)
        for (var backdropAlpha = 1; backdropAlpha <= 255; backdropAlpha++)
        {
            var w = Color.CompositeOver(
                white.WithAlpha((byte) sourceAlpha), white.WithAlpha((byte) backdropAlpha), BlendingModes.Default);
            var b = Color.CompositeOver(
                black.WithAlpha((byte) sourceAlpha), black.WithAlpha((byte) backdropAlpha), BlendingModes.Default);

            Assert.True(w is { Red: 255, Green: 255, Blue: 255 }, $"white over white at ({sourceAlpha}, {backdropAlpha}) = {w}");
            Assert.True(b is { Red: 0, Green: 0, Blue: 0 }, $"black over black at ({sourceAlpha}, {backdropAlpha}) = {b}");
            Assert.True(w.Alpha >= sourceAlpha && w.Alpha >= backdropAlpha, $"alpha shrank at ({sourceAlpha}, {backdropAlpha}) = {w.Alpha}");
        }
    }

    // ---- Degenerate operands ----

    [Fact]
    public void TransparentSource_ReturnsBackdropVerbatimIncludingItsAlpha()
    {
        var backdrop = Color.FromRgba(10, 20, 30, 90);
        Assert.Equal(backdrop, Color.CompositeOver(Color.Transparent, backdrop, BlendingModes.Default));
        Assert.Equal(Color.Transparent, Color.CompositeOver(Color.Transparent, Color.Transparent, BlendingModes.Default));
    }

    [Fact]
    public void TransparentBackdrop_ReturnsSourceVerbatimIncludingItsAlpha()
    {
        var source = Color.FromRgba(200, 100, 50, 77);
        Assert.Equal(source, Color.CompositeOver(source, Color.Transparent, BlendingModes.Default));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NonRgbOperands_MatchCompositeExactly(bool sourceIsNonRgb, bool backdropIsNonRgb)
    {
        Color[] nonRgb = [Color.Default, Color.FromPalette(3)];

        foreach (var mode in AllModes)
        foreach (var substitute in nonRgb)
        foreach (var alpha in new byte[] { 1, 64, 128, 200, 255 })
        {
            var source = sourceIsNonRgb ? substitute : Color.FromRgba(200, 100, 50, alpha);
            var backdrop = backdropIsNonRgb ? substitute : Color.FromRgba(0, 0, 255, alpha);

            Assert.Equal(Color.Composite(source, backdrop, mode), Color.CompositeOver(source, backdrop, mode));
        }
    }

    [Fact]
    public void TranslucentSourceOverPaletteBackdrop_FlattensLikeComposite()
    {
        // A palette backdrop has no RGB equivalent to mix against, so alpha is dropped, not honored —
        // the same lossy-quantization argument Composite makes.
        var result = Color.CompositeOver(Color.FromRgba(200, 100, 50, 64), Color.FromPalette(7), BlendingModes.Default);
        AssertRgba(result, 200, 100, 50, 255);
    }

    [Fact]
    public void OpaqueSource_ReplacesTheBackdropOutright()
    {
        var source = Color.FromRgb(9, 8, 7);
        Assert.Equal(source, Color.CompositeOver(source, Color.FromRgba(1, 2, 3, 40), BlendingModes.Default));
    }

    // ---- Associativity ----

    /// <summary>
    /// Porter-Duff "over" is associative in exact arithmetic, but integer sRGB rounding forbids
    /// bit-exact associativity: each nesting order truncates at different points. The measured worst
    /// case over the grid below is <b>4 LSB</b> on a channel and <b>1 LSB</b> on alpha, and it only
    /// reaches that with alphas in the single digits, where un-premultiplying amplifies a sub-unit
    /// alpha error across the whole channel range. Roughly two thirds of the grid lands within 1 LSB.
    /// Only <see cref="BlendingModes.SourceOver"/> is swept: the non-trivial modes are not associative
    /// even in exact arithmetic, so a deviation there would say nothing about the compositing math.
    /// </summary>
    [Fact]
    public void NestingOrder_AgreesWithinTheDocumentedRoundingTolerance()
    {
        byte[] alphas = [1, 17, 64, 85, 128, 170, 200, 254];

        var worstChannel = 0;
        var worstAlpha = 0;
        var worstOpaqueTail = 0;

        foreach (var ca in Channels)
        foreach (var cb in Channels)
        foreach (var cc in Channels)
        foreach (var aa in alphas)
        foreach (var ab in alphas)
        foreach (var ac in alphas.Append<byte>(255))
        {
            var a = Color.FromRgba(ca, 0, (byte) (255 - ca), aa);
            var b = Color.FromRgba(0, cb, (byte) (255 - cb), ab);
            var c = Color.FromRgba(cc, (byte) (255 - cc), 0, ac);

            var right = Color.CompositeOver(a, Color.CompositeOver(b, c, BlendingModes.Default), BlendingModes.Default);
            var left = Color.CompositeOver(Color.CompositeOver(a, b, BlendingModes.Default), c, BlendingModes.Default);

            var channelDeviation = Math.Max(
                Math.Abs(left.Red - right.Red),
                Math.Max(Math.Abs(left.Green - right.Green), Math.Abs(left.Blue - right.Blue)));

            worstChannel = Math.Max(worstChannel, channelDeviation);
            worstAlpha = Math.Max(worstAlpha, Math.Abs(left.Alpha - right.Alpha));

            // The case that actually ships: the innermost backdrop is the opaque final target.
            if (ac == 255)
                worstOpaqueTail = Math.Max(worstOpaqueTail, channelDeviation);
        }

        Assert.True(worstChannel <= 4, $"channel deviation grew to {worstChannel}");
        Assert.True(worstAlpha <= 1, $"alpha deviation grew to {worstAlpha}");
        Assert.True(worstOpaqueTail <= 2, $"deviation over an opaque tail grew to {worstOpaqueTail}");
    }
}
