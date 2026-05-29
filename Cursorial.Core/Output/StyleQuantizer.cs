using Cursorial.Output.Capabilities;

namespace Cursorial.Output;

/// <summary>
/// Capability-aware <see cref="Style"/> adapter. Applies the target terminal's
/// <see cref="OutputCapabilities"/> to a style — quantizes colors to the available depth,
/// folds extended underline shapes to <see cref="UnderlineStyle.Single"/> when the terminal
/// doesn't support the extended forms, and drops attributes the terminal doesn't honor.
/// </summary>
/// <remarks>
/// <para>
/// Separating this from <see cref="SgrEncoder"/> means the encoder stays pure: it emits exactly
/// what the <see cref="Style"/> describes, with no capability-conditional branches. A typical
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
    public Style Quantize(in Style style)
    {
        var fg = QuantizeColor(style.Foreground);
        var bg = QuantizeColor(style.Background);

        var attrs = QuantizeAttributes(style.Attributes);

        UnderlineStyle underlineStyle = style.UnderlineStyle;

        if (!_cachedCapabilities.HasFlag(CachedCapabilities.ExtendedUnderline) && underlineStyle != UnderlineStyle.Single)
            underlineStyle = UnderlineStyle.Single;

        Color underlineColor = style.UnderlineColor;

        if (!_cachedCapabilities.HasFlag(CachedCapabilities.ColoredUnderline) && !underlineColor.IsDefault)
            underlineColor = Color.Default;
        else if (!underlineColor.IsDefault)
            underlineColor = QuantizeColor(underlineColor);

        var link = Hyperlink.None;

        if (_cachedCapabilities.HasFlag(CachedCapabilities.Hyperlinks) && style.Hyperlink is { Uri: not null } hyperlink)
            link = hyperlink;

        return new Style(fg, bg, attrs, underlineStyle, underlineColor, link);
    }

    private Color QuantizeColor(Color color)
    {
        var depth = _cachedCapabilities.HasFlag(CachedCapabilities.Truecolor)
                        ? ColorDepth.Truecolor
                        : _cachedCapabilities.HasFlag(CachedCapabilities.Ansi256)
                            ? ColorDepth.Ansi256
                            : _cachedCapabilities.HasFlag(CachedCapabilities.Ansi16)
                                ? ColorDepth.Ansi16
                                : ColorDepth.NoColor;

        if (color == Color.Transparent)
            return depth == ColorDepth.Truecolor ? color : Color.Default;

        // @formatter:off
        return color.Kind switch
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
        // @formatter:on
    }

    private TextAttributes QuantizeAttributes(TextAttributes attributes)
    {
        // Drop attributes the terminal doesn't honor. Bold/Faint/Blink/Inverse/Hidden are
        // assumed present on anything with SGR support at all — we surface only the attributes
        // whose capability is reported separately.
        if (!_cachedCapabilities.HasFlag(CachedCapabilities.Italic)) attributes &= ~TextAttributes.Italic;
        if (!_cachedCapabilities.HasFlag(CachedCapabilities.Underline)) attributes &= ~TextAttributes.Underline;
        if (!_cachedCapabilities.HasFlag(CachedCapabilities.Strikethrough)) attributes &= ~TextAttributes.Strikethrough;
        if (!_cachedCapabilities.HasFlag(CachedCapabilities.Overline)) attributes &= ~TextAttributes.Overline;

        return attributes;
    }

    // ---- Color quantization tables ----

    /// <summary>
    /// Map an RGB value into the xterm 256-color palette. Searches the 6×6×6 RGB cube
    /// (indices 16–231) and the 24-step grayscale ramp (232–255) and returns the closer of the
    /// two by sum-of-absolute-differences. The cube is the dominant choice for chromatic input;
    /// grayscale wins for near-monochrome input where the cube's stops are too coarse.
    /// </summary>
    public static byte NearestPaletteIndex(byte red, byte green, byte blue)
    {
        // xterm cube stops: 0, 95, 135, 175, 215, 255.
        int ri = CubeAxisIndex(red);
        int gi = CubeAxisIndex(green);
        int bi = CubeAxisIndex(blue);
        byte cubeIndex = (byte) (16 + 36 * ri + 6 * gi + bi);
        int cubeR = CubeAxisValue(ri);
        int cubeG = CubeAxisValue(gi);
        int cubeB = CubeAxisValue(bi);
        int cubeDist = Diff(red, cubeR) + Diff(green, cubeG) + Diff(blue, cubeB);

        // Grayscale ramp stops: 8 + n*10 for n=0..23 → 8, 18, …, 238.
        int avg = (red + green + blue) / 3;
        int grayIndex;
        int grayValue;

        if (avg < 8)
        {
            grayIndex = 0;
            grayValue = 8;
        }
        else if (avg > 238)
        {
            grayIndex = 23;
            grayValue = 238;
        }
        else
        {
            grayIndex = (avg - 8 + 5) / 10; // round half-up.
            grayValue = 8 + grayIndex * 10;
        }

        int grayDist = Diff(red, grayValue) + Diff(green, grayValue) + Diff(blue, grayValue);

        return grayDist < cubeDist ? (byte) (232 + grayIndex) : cubeIndex;
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

        if (index < 232)
        {
            int n = index - 16;
            int ri = n / 36;
            int gi = (n / 6) % 6;
            int bi = n % 6;
            return NearestAnsi16Index((byte) CubeAxisValue(ri), (byte) CubeAxisValue(gi), (byte) CubeAxisValue(bi));
        }

        // Grayscale: 0 → black, 23 → white, with a midline crossover.
        int grayValue = 8 + (index - 232) * 10;
        return NearestAnsi16Index((byte) grayValue, (byte) grayValue, (byte) grayValue);
    }

    private static int CubeAxisIndex(byte value)
    {
        // Map a channel value to the nearest of 0, 95, 135, 175, 215, 255 and return the index.
        if (value < 48) return 0;
        if (value < 115) return 1;
        if (value < 155) return 2;
        if (value < 195) return 3;
        if (value < 235) return 4;
        return 5;
    }

    private static int CubeAxisValue(int axisIndex) => axisIndex == 0 ? 0 : 55 + axisIndex * 40;

    private static int Diff(int a, int b) => a > b ? a - b : b - a;
}