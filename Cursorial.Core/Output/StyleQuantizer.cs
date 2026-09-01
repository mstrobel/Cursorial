using System.Runtime.CompilerServices;

using Cursorial.Media;
using Cursorial.Output.Capabilities;
using Cursorial.Text;

namespace Cursorial.Output;

/// <summary>
/// Capability-aware <see cref="CellStyle"/> adapter. Applies the target terminal's
/// <see cref="OutputCapabilities"/> to a style — quantizes colors to the available depth,
/// folds extended underline shapes to <see cref="UnderlineStyle.Single"/> when the terminal
/// doesn't support the extended forms, and drops attributes the terminal doesn't honor.
/// </summary>
/// <remarks>
/// <para>
/// Separating this from <see cref="SgrEncoder"/> means the encoder stays pure: it emits exactly
/// what the <see cref="CellStyle"/> describes, with no capability-conditional branches. A typical
/// pipeline is <c>style → StyleQuantizer.Quantize → SgrEncoder.Write{Absolute|Delta}</c>.
/// </para>
/// <para>
/// The quantizer is constructed with a capability set and produces an unchanged copy of any
/// already-renderable style. The same instance is safe to reuse for many styles; it has no
/// internal state beyond the capabilities.
/// </para>
/// </remarks>
public sealed class StyleQuantizer
{
    [Flags]
    private enum CachedCapabilities
    {
        None = 0,
        ExtendedUnderline = 1 << 0,
        ColoredUnderline = 1 << 1,
        Hyperlinks = 1 << 2,
        Ansi16 = 1 << 3,
        Ansi256 = 1 << 4,
        Truecolor = 1 << 5,
        Italic = 1 << 6,
        Underline = 1 << 7,
        Strikethrough = 1 << 8,
        Overline = 1 << 9
        
    }

    private readonly OutputCapabilities _capabilities;
    private readonly CachedCapabilities _cachedCapabilities;

    public StyleQuantizer(OutputCapabilities capabilities)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _cachedCapabilities = CachedCapabilities.None;

        var styling = _capabilities.Styling;
        var color = _capabilities.Color;

        if (styling.ExtendedUnderline)
            _cachedCapabilities |= CachedCapabilities.ExtendedUnderline;
        if (styling.ColoredUnderline)
            _cachedCapabilities |= CachedCapabilities.ColoredUnderline;
        if (styling.Hyperlinks)
            _cachedCapabilities |= CachedCapabilities.Hyperlinks;
        if (styling.Italic)
            _cachedCapabilities |= CachedCapabilities.Italic;
        if (styling.Underline)
            _cachedCapabilities |= CachedCapabilities.Underline;
        if (styling.Strikethrough)
            _cachedCapabilities |= CachedCapabilities.Strikethrough;
        if (styling.Overline)
            _cachedCapabilities |= CachedCapabilities.Overline;

        if (color.Depth >= ColorDepth.Ansi16)
            _cachedCapabilities |= CachedCapabilities.Ansi16;
        if (color.Depth >= ColorDepth.Ansi256)
            _cachedCapabilities |= CachedCapabilities.Ansi256;
        if (color.Depth >= ColorDepth.Truecolor)
            _cachedCapabilities |= CachedCapabilities.Truecolor;
    }

    /// <summary>The capability set this quantizer was constructed with.</summary>
    public OutputCapabilities Capabilities => _capabilities;

    /// <summary>Return a copy of <paramref name="style"/> that the target terminal can render verbatim.</summary>
    public CellStyle Quantize(in CellStyle style)
    {
        var fg = QuantizeColor(style.Foreground);
        var bg = QuantizeColor(style.Background);

        var attrs = QuantizeAttributes(style.Attributes);

        UnderlineStyle underlineStyle = style.UnderlineStyle;

        ref readonly var cachedCapabilities = ref _cachedCapabilities;

        if (!cachedCapabilities.HasFlag(CachedCapabilities.ExtendedUnderline) && underlineStyle != UnderlineStyle.Single)
            underlineStyle = UnderlineStyle.Single;

        Color underlineColor = style.UnderlineColor;

        if (!cachedCapabilities.HasFlag(CachedCapabilities.ColoredUnderline) && !underlineColor.IsDefault)
            underlineColor = Color.Default;
        else if (!underlineColor.IsDefault)
            underlineColor = QuantizeColor(underlineColor);

        var link = Hyperlink.None;

        if (cachedCapabilities.HasFlag(CachedCapabilities.Hyperlinks) && style.Hyperlink is { Uri: not null } hyperlink)
            link = hyperlink;

        return new CellStyle(fg, bg, attrs, underlineStyle, underlineColor, link);
    }

    /// <summary>
    /// Quantize <paramref name="style"/> like <see cref="Quantize(in CellStyle)"/>, but apply ordered
    /// (Bayer) dithering to <see cref="ColorKind.Rgb"/> colors before palette reduction, using the
    /// destination cell's position to pick the dither phase. Spatially perturbing the color before
    /// snapping it to the nearest palette entry trades a stippled texture for perceived depth, so a
    /// smooth RGB gradient stops banding into flat stripes on a 256- or 16-color terminal.
    /// </summary>
    /// <remarks>
    /// On a <see cref="ColorDepth.Truecolor"/> target this is identical to <see cref="Quantize(in CellStyle)"/>
    /// (no reduction happens, so there is nothing to dither and perturbation would only corrupt the exact
    /// color); on <see cref="ColorDepth.NoColor"/> color is discarded either way. Non-RGB
    /// (<see cref="ColorKind.Palette"/> / <see cref="ColorKind.Default"/>) and <see cref="Color.Transparent"/>
    /// colors pass through unperturbed — there is no continuous RGB value to dither. Pure given
    /// <c>(style, column, row)</c>: the same inputs always yield the same style, which keeps the frame
    /// renderer's front/back diff stable.
    /// </remarks>
    /// <param name="style">The style whose foreground / background / underline colors are dithered.</param>
    /// <param name="column">Destination terminal column (0-based) — selects the Bayer X phase.</param>
    /// <param name="row">Destination terminal row (0-based) — selects the Bayer Y phase.</param>
    public CellStyle QuantizeDithered(in CellStyle style, int column, int row)
    {
        // Mirror of Quantize: identical structure and capability gating; only the three color sites swap
        // QuantizeColor → DitherColor. Keep the two in sync.
        var fg = DitherColor(style.Foreground, column, row);
        var bg = DitherColor(style.Background, column, row);

        var attrs = QuantizeAttributes(style.Attributes);

        UnderlineStyle underlineStyle = style.UnderlineStyle;

        ref readonly var cachedCapabilities = ref _cachedCapabilities;

        if (!cachedCapabilities.HasFlag(CachedCapabilities.ExtendedUnderline) && underlineStyle != UnderlineStyle.Single)
            underlineStyle = UnderlineStyle.Single;

        Color underlineColor = style.UnderlineColor;

        if (!cachedCapabilities.HasFlag(CachedCapabilities.ColoredUnderline) && !underlineColor.IsDefault)
            underlineColor = Color.Default;
        else if (!underlineColor.IsDefault)
            underlineColor = DitherColor(underlineColor, column, row);

        var link = Hyperlink.None;

        if (cachedCapabilities.HasFlag(CachedCapabilities.Hyperlinks) && style.Hyperlink is { Uri: not null } hyperlink)
            link = hyperlink;

        return new CellStyle(fg, bg, attrs, underlineStyle, underlineColor, link);
    }

    // Canonical 4×4 Bayer ordered-dither threshold matrix (values 0–15), indexed by (row&3, column&3).
    private static readonly int[] Bayer4 =
    [
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5,
    ];

    // Perturbation amplitude (PINNED). Chosen so the per-cell offset (~ ±22) straddles the widest xterm
    // cube interval (215→255 = 40) without skipping a stop — adjacent cells land on either side of a
    // palette boundary, which is what produces the stipple. The primary tuning knob; oracle tests lock it.
    private const double DitherSpread = 48.0;

    private const int GrayTableStart = 232;

    // Ordered-dither a single color, then quantize. Only RGB at a reducing depth is perturbed: Transparent
    // and non-RGB pass straight through QuantizeColor (nothing continuous to dither), and a Truecolor target
    // needs no reduction — perturbing it would corrupt the exact color and break the "dither is a no-op at
    // full depth" guarantee.
    private Color DitherColor(Color color, int column, int row)
    {
        if (color.Kind != ColorKind.Rgb ||
            color == Color.Transparent || 
            _cachedCapabilities.HasFlag(CachedCapabilities.Truecolor))
        {
            return QuantizeColor(color);
        }

        int threshold = Bayer4[(row & 3) * 4 + (column & 3)];   // 0..15
        double delta = ((threshold + 0.5) / 16.0 - 0.5) * DitherSpread;

        byte r = Perturb(color.Red, delta);
        byte g = Perturb(color.Green, delta);
        byte b = Perturb(color.Blue, delta);

        return QuantizeColor(Color.FromRgb(r, g, b));
    }

    private static byte Perturb(byte value, double delta) => (byte) Math.Clamp(value + delta, 0.0, 255.0);

    private Color QuantizeColor(Color color)
    {
        ref readonly var cachedCapabilities = ref _cachedCapabilities;

        var depth = cachedCapabilities.HasFlag(CachedCapabilities.Truecolor)
                        ? ColorDepth.Truecolor
                        : cachedCapabilities.HasFlag(CachedCapabilities.Ansi256)
                            ? ColorDepth.Ansi256
                            : cachedCapabilities.HasFlag(CachedCapabilities.Ansi16)
                                ? ColorDepth.Ansi16
                                : ColorDepth.NoColor;

        if (color == Color.Transparent)
            return depth == ColorDepth.Truecolor ? color : Color.Default;

        // @formatter:off
        var result = color.Kind switch
                     {
                         ColorKind.Default                                  => color,
                         _ when depth == ColorDepth.NoColor                 => Color.Default,
                         ColorKind.Rgb when depth == ColorDepth.Truecolor   => color,
                         ColorKind.Rgb when depth == ColorDepth.Ansi256     => Color.FromPalette(NearestPaletteIndex(color.Red, color.Green, color.Blue)),
                         ColorKind.Rgb /* Ansi16 */                         => Color.FromPalette(NearestAnsi16Index(color.Red, color.Green, color.Blue)),
                         ColorKind.Palette when depth >= ColorDepth.Ansi256 => color,
                         ColorKind.Palette /* Ansi16 */                     => color.PaletteIndex < 16 
                                                                                   ? color 
                                                                                   : Color.FromPalette(PaletteIndexToAnsi16(color.PaletteIndex)),
                         _                                                  => color
                     };

        if (result == Color.FromPalette(255))
            return Color.FromPalette(254);

        return result;
        // @formatter:on
    }

    private TextAttributes QuantizeAttributes(TextAttributes attributes)
    {
        ref readonly var cachedCapabilities = ref _cachedCapabilities;

        // Drop attributes the terminal doesn't honor. Bold/Faint/Blink/Inverse/Hidden are
        // assumed present on anything with SGR support at all — we surface only the attributes
        // whose capability is reported separately.
        if (!cachedCapabilities.HasFlag(CachedCapabilities.Italic)) attributes &= ~TextAttributes.Italic;
        if (!cachedCapabilities.HasFlag(CachedCapabilities.Underline)) attributes &= ~TextAttributes.Underline;
        if (!cachedCapabilities.HasFlag(CachedCapabilities.Strikethrough)) attributes &= ~TextAttributes.Strikethrough;
        if (!cachedCapabilities.HasFlag(CachedCapabilities.Overline)) attributes &= ~TextAttributes.Overline;

        return attributes;
    }

    // ---- Color quantization tables ----

    /// <summary>
    /// Map an RGB value into the xterm 256-color palette. Searches the 6×6×6 RGB cube
    /// (indices 16–231) and the 24-step grayscale ramp (232–255) and returns the closer of the
    /// two by sum-of-absolute-differences. The cube is the dominant choice for chromatic input;
    /// grayscale wins for near-monochrome input where the cube's stops are too coarse.
    /// </summary>
    public static byte NearestPaletteIndex(byte r, byte g, byte b)
    {
        var rgb = ((uint)r << 16) + ((uint)g << 8) + b;

        if (r == g && g == b) return GrayIndex(rgb & 0xFFu);

        uint cube = CubeAxisComponent(r, CubeAxis.Red) +
                    CubeAxisComponent(g, CubeAxis.Green) +
                    CubeAxisComponent(b, CubeAxis.Blue);

        var cubeDistance = Distance(rgb, cube);

        var grayIndex = GrayIndex(Luminance(rgb));
        var grayDistance  = Distance(rgb, RgbFromAnsi256(grayIndex));

        if (cubeDistance < grayDistance)
        {
            var cubeIndex = (byte) (cube >> 24);
            return cubeIndex;
        }

        return grayIndex;
    }

    private static byte GrayIndex(uint value)
    {
        const byte blacklistStart = 233; // first 255 entry
        const byte blacklistEnd = 246;   // last 255 entry
        const byte altGrayLower = 254;   // #e4e4e4
        const byte altGrayHigher = 231;  // #ffffff

        var grayIndex = GrayLookupTable[value];
        
        // HACK: kitty assigns #abcdef instead of #eeeeee at Palette(255) for some reason.
        //       Unconditionally disallow that palette index and switch to 254 or 231 (whichever is closer).
        if (grayIndex == 255)
            grayIndex = Diff(value, blacklistStart) < Diff(value, blacklistEnd) ? altGrayLower : altGrayHigher;

        return grayIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Diff(uint a, uint b) => a > b ? a - b : b - a;
    
    private enum CubeAxis
    {
        Red,
        Green,
        Blue
    }

    private static readonly byte[] CubeThresholdsRed = [38, 115, 155, 196, 235];
    private static readonly byte[] CubeThresholdsGreen = [36, 116, 154, 195, 235];
    private static readonly byte[] CubeThresholdsBlue = [35, 115, 155, 195, 235];
    private static readonly byte[] CubeAxisRamp = [0, 95, 135, 175, 215, 255];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Distance(uint x, uint y)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint R(uint c) => (c >> 16) & 0xFFu;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint G(uint c) => (c >> 8) & 0xFFu;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint B(uint c) => c & 0xFFu;
        
        var rSum= R(x) + R(y);
        var r = R(x) - R(y);
        var g = G(x) - G(y);
        var b = B(x) - B(y);

        return (1024 + rSum) * r * r + 2048 * g * g + (1534 - rSum) * b * b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Luminance(uint rgb)
    {
        var v = 3567664u * (rgb >> 16 & 0xff) + 11998547u * (rgb >> 8 & 0xff) + 1211005u * (rgb & 0xff);
        return (byte) ((v + (1 << 23)) >> 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubeIndex(uint i) => CubeAxisRamp[i];

    private static uint CubeAxisComponent(byte v, CubeAxis axis)
    {
        var thresholds = AxisThresholds(axis);

        // @formatter:off
        if (v < thresholds[0]) { return CubeAxisComponent(0,   0, axis); }
        if (v < thresholds[1]) { return CubeAxisComponent(1,  95, axis); }
        if (v < thresholds[2]) { return CubeAxisComponent(2, 135, axis); }
        if (v < thresholds[3]) { return CubeAxisComponent(3, 175, axis); }
        if (v < thresholds[4]) { return CubeAxisComponent(4, 215, axis); }
        /* else */             { return CubeAxisComponent(5, 255, axis); }
        // @formatter:on
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte[] AxisThresholds(CubeAxis axis)
    {
        return axis switch
               {
                   CubeAxis.Red   => CubeThresholdsRed,
                   CubeAxis.Green => CubeThresholdsGreen,
                   _              => CubeThresholdsBlue
               };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubeAxisComponent(uint i, uint v, CubeAxis axis)
    {
        return axis switch
               {
                   CubeAxis.Red   => CubeComponentRed(i, v),
                   CubeAxis.Green => CubeComponentGreen(i, v),
                   _              => CubeComponentBlue(i, v)
               };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubeComponentBlue(uint i, uint v) => (i << 24) | v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubeComponentGreen(uint i, uint v) => ((i * 6) << 24) | (v << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CubeComponentRed(uint i, uint v) => ((i * 36 + 16) << 24) | (v << 16);

    private static uint RgbFromAnsi256(uint i)
    {
        if (i < 16)
            return SystemColors[i];

        uint index;

        if (i < 232)
        {
            index = i - 16;

            return CubeIndex(index / 36) << 16 |
                   CubeIndex(index / 6 % 6) << 8 |
                   CubeIndex(index % 6);
        }

        index = (i - 232) * 10 + 8;
        return index * 0x010101u;
    }

    /// <summary>
    /// Map an RGB value into the standard 16-color ANSI palette (indices 0–15). Uses the
    /// approximation that the 8 standard colors are RGB combinations of {0, 128} and the
    /// 8 bright colors use {0, 255}; the index is determined by which quadrant the input
    /// occupies.
    /// </summary>
    public static byte NearestAnsi16Index(byte red, byte green, byte blue)
    {
        // Threshold for "channel is on" — picked so #c0c0c0 (light gray) maps to 7 rather
        // than 15. Tunable; this matches what most terminals do.
        int rOn = red >= 128 ? 1 : 0;
        int gOn = green >= 128 ? 1 : 0;
        int bOn = blue >= 128 ? 1 : 0;
        int baseIndex = rOn | (gOn << 1) | (bOn << 2);

        // Bright bit if any channel is very bright (saturated).
        bool bright = red > 192 || green > 192 || blue > 192;
        return (byte) (bright ? baseIndex + 8 : baseIndex);
    }

    /// <summary>
    /// Approximate mapping from a 256-color palette index to the nearest 16-color index.
    /// Cube cells (16–231) resolve through their RGB equivalent; grayscale (232–255) uses
    /// luminance buckets.
    /// </summary>
    public static byte PaletteIndexToAnsi16(byte index)
    {
        if (index < 16) return index;

        if (index < GrayTableStart)
        {
            int n = index - 16;
            int ri = n / 36;
            int gi = (n / 6) % 6;
            int bi = n % 6;
            return NearestAnsi16Index((byte) CubeAxisValue(ri), (byte) CubeAxisValue(gi), (byte) CubeAxisValue(bi));
        }

        // Grayscale: 0 → black, 23 → white, with a midline crossover.
        int grayValue = 8 + (index - GrayTableStart) * 10;
        return NearestAnsi16Index((byte) grayValue, (byte) grayValue, (byte) grayValue);
    }

    private static int CubeAxisValue(int axisIndex) => axisIndex == 0 ? 0 : 55 + axisIndex * 40;

    // @formatter:off
    private static readonly byte[] GrayLookupTable =
    [
        16, 16, 16, 16, 16, 232, 232, 232, 232, 232, 232, 232, 232, 232, 233, 233, 233, 233, 233, 233,
        233, 233, 233, 233, 234, 234, 234, 234, 234, 234, 234, 234, 234, 234, 235, 235, 235, 235, 235,
        235, 235, 235, 235, 235, 236, 236, 236, 236, 236, 236, 236, 236, 236, 236, 237, 237, 237, 237,
        237, 237, 237, 237, 237, 237, 238, 238, 238, 238, 238, 238, 238, 238, 238, 238, 239, 239, 239,
        239, 239, 239, 239, 239, 239, 239, 240, 240, 240, 240, 240, 240, 240, 240, 59, 59, 59, 59, 59,
        241, 241, 241, 241, 241, 241, 241, 242, 242, 242, 242, 242, 242, 242, 242, 242, 242, 243, 243,
        243, 243, 243, 243, 243, 243, 243, 244, 244, 244, 244, 244, 244, 244, 244, 244, 102, 102, 102,
        102, 102, 245, 245, 245, 245, 245, 245, 246, 246, 246, 246, 246, 246, 246, 246, 246, 246, 247,
        247, 247, 247, 247, 247, 247, 247, 247, 247, 248, 248, 248, 248, 248, 248, 248, 248, 248, 145,
        145, 145, 145, 145, 249, 249, 249, 249, 249, 249, 250, 250, 250, 250, 250, 250, 250, 250, 250,
        250, 251, 251, 251, 251, 251, 251, 251, 251, 251, 251, 252, 252, 252, 252, 252, 252, 252, 252,
        252, 188, 188, 188, 188, 188, 253, 253, 253, 253, 253, 253, 254, 254, 254, 254, 254, 254, 254,
        254, 254, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 231, 231,
        231, 231, 231, 231, 231, 231, 231
    ];

    private static readonly uint[] SystemColors =
    [
        0x000000u, 0xCD0000u, 0x00CD00u, 0xCDCD00u, 0x0000EEu, 0xCD00CDu, 0x00CDCDu, 0xE5E5E5u,
        0x7F7F7Fu, 0xFF0000u, 0x00FF00u, 0xFFFF00u, 0x5C5CFFu, 0xFF00FFu, 0x00FFFFu, 0xFFFFFFu
    ];
    // @formatter:on
}