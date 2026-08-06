namespace Cursorial.Media;

/// <summary>
/// A terminal color in one of three representations: the terminal's <see cref="ColorKind.Default"/>
/// foreground/background, an indexed entry into the 256-color <see cref="ColorKind.Palette"/>,
/// or a 24-bit <see cref="ColorKind.Rgb"/> truecolor value, optionally combined with an
/// <see cref="Alpha"/> channel that drives layered-draw compositing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Color"/> is a value type with no allocation. Two <see cref="Color"/> values
/// compare equal when their kind, payload bytes, and alpha all match.
/// </para>
/// <para>
/// Use the static factories rather than the parameterized constructor: <see cref="Default"/>,
/// <see cref="FromPalette"/>, <see cref="FromRgb"/>, <see cref="FromRgba"/>. The
/// default-constructed <see cref="Color"/> is equivalent to <see cref="Default"/>.
/// </para>
/// <para>
/// <b>Alpha semantics.</b> <see cref="Alpha"/> only takes effect during layered drawing into a
/// cell buffer where the active blending mode composites the source color over the existing
/// cell's color. Alpha is meaningful only for <see cref="ColorKind.Rgb"/> colors — non-RGB
/// kinds short-circuit through compositing (we don't know their RGB equivalent to mix against).
/// Terminal output is always opaque: the cell buffer stores the composited result, and the
/// renderer emits SGR codes for it as if it were a fully opaque color.
/// </para>
/// <para>
/// Capability-aware downgrade (RGB→palette on terminals without truecolor, palette→16 on
/// terminals without 256 colors) is the job of <c>StyleQuantizer</c>, not of this type. A
/// <see cref="Color"/> always carries the value the application requested; rendering decides
/// whether to honor it as-is or quantize.
/// </para>
/// </remarks>
public readonly record struct Color : ISpanFormattable
{
    private Color(ColorKind kind, byte b0, byte b1, byte b2, byte alpha)
    {
        Kind = kind;
        PaletteIndex = b0;
        Green = b1;
        Blue = b2;
        Alpha = alpha;
    }

    /// <summary>The representation of this color.</summary>
    public ColorKind Kind { get; }

    /// <summary>
    /// The terminal's default color for the slot this is assigned to (foreground or background).
    /// <see cref="Alpha"/> is held at 0 to keep this value equivalent to <c>default(Color)</c>;
    /// alpha is not applied to <see cref="ColorKind.Default"/> colors anyway.
    /// </summary>
    public static Color Default { get; } = new(ColorKind.Default, 0, 0, 0, 0);

    /// <summary>Represents a fully transparent color in the RGB color space.</summary>
    public static Color Transparent { get; } = new(ColorKind.Rgb, 0, 0, 0, 0);

    /// <summary>
    /// The palette index. Defined only when <see cref="Kind"/> is <see cref="ColorKind.Palette"/>;
    /// reads as 0 for other kinds (callers should guard on <see cref="Kind"/>).
    /// </summary>
    public byte PaletteIndex { get; }

    /// <summary>Red component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Red => PaletteIndex;

    /// <summary>Green component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Green { get; }

    /// <summary>Blue component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Blue { get; }

    /// <summary>
    /// Alpha channel. 0 = fully transparent (the source completely yields to backdrop during
    /// compositing); 255 = fully opaque (the source replaces backdrop). Meaningful only for
    /// <see cref="ColorKind.Rgb"/> colors — non-RGB kinds short-circuit through compositing
    /// regardless of this value.
    /// </summary>
    public byte Alpha { get; }
    
    public uint RgbaValue => (uint)Red << 16 | (uint)Green << 8 | Blue;

    /// <summary>True when this color requests the terminal's default for its slot.</summary>
    public bool IsDefault => Kind == ColorKind.Default;

    /// <summary>True when the color is fully opaque (alpha = 255) or its kind ignores alpha.</summary>
    public bool IsOpaque => Kind != ColorKind.Rgb || Alpha == 255;

    /// <summary>True when the color is fully transparent (alpha = 0).</summary>
    public bool IsTransparent => this is { Kind: ColorKind.Rgb, Alpha: 0 };

    /// <summary>Construct a palette color from a 0–255 index. Indices 0–15 are the ANSI base colors.</summary>
    public static Color FromPalette(byte index)
    {
        return new Color(ColorKind.Palette, index, 0, 0, 255);
    }

    /// <summary>Construct a fully opaque 24-bit truecolor value.</summary>
    public static Color FromRgb(byte red, byte green, byte blue)
    {
        return new Color(ColorKind.Rgb, red, green, blue, 255);
    }

    /// <summary>Construct a 24-bit truecolor value from a hex code.</summary>
    public static Color FromHex(in ReadOnlySpan<char> hexCode)
    {
        return TryParseHex(hexCode, out Color color, out Exception? error) ? color : throw error!;
    }

    /// <summary>Construct a 24-bit truecolor value from a hex code.</summary>
    public static bool TryParseHex(in ReadOnlySpan<char> hexCode, out Color color, out Exception? error)
    {
        color = default;
        error = null;

        var span = hexCode;
        
        // Remove leading '#' if present
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];

        try
        {
            // Support 3-digit (#RGB) and 6-digit (#RRGGBB) hex codes
            if (span.Length == 3)
            {
                // Convert 3-digit format to 6-digit by doubling each digit
                byte r = (byte) (ParseHexDigit(span[0]) * 17);
                byte g = (byte) (ParseHexDigit(span[1]) * 17);
                byte b = (byte) (ParseHexDigit(span[2]) * 17);
                color = FromRgb(r, g, b);
                return true;
            }

            if (span.Length == 6)
            {
                byte r = (byte) ((ParseHexDigit(span[0]) << 4) | ParseHexDigit(span[1]));
                byte g = (byte) ((ParseHexDigit(span[2]) << 4) | ParseHexDigit(span[3]));
                byte b = (byte) ((ParseHexDigit(span[4]) << 4) | ParseHexDigit(span[5]));
                color = FromRgb(r, g, b);
                return true;
            }

            if (span.Length == 8)
            {
                byte r = (byte) ((ParseHexDigit(span[0]) << 4) | ParseHexDigit(span[1]));
                byte g = (byte) ((ParseHexDigit(span[2]) << 4) | ParseHexDigit(span[3]));
                byte b = (byte) ((ParseHexDigit(span[4]) << 4) | ParseHexDigit(span[5]));
                byte a = (byte) ((ParseHexDigit(span[6]) << 4) | ParseHexDigit(span[7]));
                color = FromRgba(r, g, b, a);
                return true;
            }
        }
        catch (FormatException inner)
        {
            error = inner;
            return false;
        }

        error = new FormatException($"Invalid hex code format. Expected #RGB, #RRGGBB, or #RRGGBBAA, but got '{span}'.");
        return false;

        static int ParseHexDigit(char c)
        {
            if (c is >= '0' and <= '9')
                return c - '0';
            if (c is >= 'a' and <= 'f')
                return c - 'a' + 10;
            if (c is >= 'A' and <= 'F')
                return c - 'A' + 10;
            throw new FormatException($"Invalid hex digit: '{c}'");
        }
    }

    /// <summary>
    /// Construct a 24-bit truecolor value with an explicit alpha channel. <paramref name="alpha"/>
    /// of 255 is fully opaque (equivalent to <see cref="FromRgb"/>); 0 is fully transparent
    /// (compositing returns the backdrop unchanged). Intermediate values mix the blended source
    /// color with the backdrop linearly.
    /// </summary>
    public static Color FromRgba(byte red, byte green, byte blue, byte alpha)
    {
        return new Color(ColorKind.Rgb, red, green, blue, alpha);
    }

    /// <summary>
    /// Construct a 24-bit truecolor value from its HSV components with an explicit alpha channel.
    /// <paramref name="alpha"/> of 255 is fully opaque (equivalent to <see cref="FromRgb"/>); 0 is
    /// fully transparent (compositing returns the backdrop unchanged). Intermediate values mix the
    /// blended source color with the backdrop linearly.
    /// </summary>
    public static Color FromHsv(double hue, double saturation, double value, byte alpha = 255)
    {
        var h = hue * 6.0 % 6.0;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
                        {
                            0 => (c, x, 0.0),
                            1 => (x, c, 0.0),
                            2 => (0.0, c, x),
                            3 => (0.0, x, c),
                            4 => (x, 0.0, c),
                            _ => (c, 0.0, x),
                        };
        var m = value - c;
        // ToByteClamped rounds (not truncates) and clamps to [0,255] — matching Lerp's convention. The raw
        // (byte) cast both dropped a fractional part (≈40% of the gamut drifted ±1 on a round-trip, so
        // Brighten(0)/Darken(0) weren't the identity) and WRAPPED mod 256 on out-of-range inputs (e.g. value>1).
        return FromRgba(ToByteClamped((r + m) * 255), ToByteClamped((g + m) * 255), ToByteClamped((b + m) * 255), alpha);
    }

    /// <summary>
    /// Convert this RGB color to HSV (hue, saturation, value) representation.
    /// Returns (0, 0, 0) for non-RGB colors.
    /// </summary>
    public (double hue, double saturation, double value) ToHsv()
    {
        if (Kind != ColorKind.Rgb)
            return (0, 0, 0);

        double r = Red / 255.0;
        double g = Green / 255.0;
        double b = Blue / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            const double epsilon = 1e-10;
            
            if (Math.Abs(max - r) < epsilon)
                hue = ((g - b) / delta % 6.0) / 6.0;
            else if (Math.Abs(max - g) < epsilon)
                hue = ((b - r) / delta + 2.0) / 6.0;
            else
                hue = ((r - g) / delta + 4.0) / 6.0;

            if (hue < 0)
                hue += 1.0;
        }
        double saturation = max > 0 ? delta / max : 0;
        double value = max;

        return (hue, saturation, value);
    }

    /// <summary>
    /// Construct a 24-bit truecolor value from its HSL components with an explicit alpha channel.
    /// <paramref name="hue"/>, <paramref name="saturation"/>, and <paramref name="luminosity"/> are
    /// 0–1 fractions. <paramref name="alpha"/> of 255 is fully opaque (equivalent to
    /// <see cref="FromRgb"/>); 0 is fully transparent.
    /// </summary>
    public static Color FromHsl(double hue, double saturation, double luminosity, byte alpha = 255)
    {
        var h = hue * 6.0 % 6.0;
        var c = (1 - Math.Abs(2 * luminosity - 1)) * saturation;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
                        {
                            0 => (c, x, 0.0),
                            1 => (x, c, 0.0),
                            2 => (0.0, c, x),
                            3 => (0.0, x, c),
                            4 => (x, 0.0, c),
                            _ => (c, 0.0, x),
                        };
        var m = luminosity - c / 2;
        // ToByteClamped rounds (not truncates) and clamps to [0,255] — matching Lerp's convention. The raw
        // (byte) cast both dropped a fractional part (≈40% of the gamut drifted ±1 on a round-trip, so
        // Brighten(0)/Darken(0) weren't the identity) and WRAPPED mod 256 on out-of-range inputs (e.g. value>1).
        return FromRgba(ToByteClamped((r + m) * 255), ToByteClamped((g + m) * 255), ToByteClamped((b + m) * 255), alpha);
    }

    /// <summary>
    /// Convert this RGB color to HSL (hue, saturation, luminosity) representation.
    /// Returns (0, 0, 0) for non-RGB colors.
    /// </summary>
    public (double hue, double saturation, double luminosity) ToHsl()
    {
        if (Kind != ColorKind.Rgb)
            return (0, 0, 0);

        double r = Red / 255.0;
        double g = Green / 255.0;
        double b = Blue / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double luminosity = (max + min) / 2.0;

        double hue = 0;
        double saturation = 0;
        if (delta > 0)
        {
            const double epsilon = 1e-10;

            if (Math.Abs(max - r) < epsilon)
                hue = ((g - b) / delta % 6.0) / 6.0;
            else if (Math.Abs(max - g) < epsilon)
                hue = ((b - r) / delta + 2.0) / 6.0;
            else
                hue = ((r - g) / delta + 4.0) / 6.0;

            if (hue < 0)
                hue += 1.0;

            saturation = delta / (1 - Math.Abs(2 * luminosity - 1));
        }

        return (hue, saturation, luminosity);
    }

    /// <summary>
    /// Return a copy of this color with <see cref="Alpha"/> set to <paramref name="alpha"/>.
    /// A no-op for <see cref="ColorKind.Default"/> (alpha is meaningless there).
    /// </summary>
    public Color WithAlpha(byte alpha)
    {
        if (Kind == ColorKind.Default) return this;
        return new Color(Kind, PaletteIndex, Green, Blue, alpha);
    }

    /// <summary>
    /// Return a lighter copy of this color by raising its HSL luminosity by
    /// <paramref name="percentage"/> (a 0–1 fraction; defaults to 5%). The luminosity is clamped
    /// to <c>[0, 1]</c>, so brightening a near-white color saturates at white. <see cref="Alpha"/>
    /// is preserved. Adjusting luminosity is only meaningful for <see cref="ColorKind.Rgb"/>;
    /// palette and default colors are returned unchanged (matching <see cref="Lerp"/>/<see cref="Composite"/>).
    /// </summary>
    public Color Brighten(double percentage = 0.05)
    {
        if (Kind != ColorKind.Rgb)
            return this;

        var (h, s, l) = ToHsl();
        return FromHsl(h, s, Math.Clamp(l + percentage, 0.0, 1.0), Alpha);
    }

    /// <summary>
    /// Return a darker copy of this color by lowering its HSL luminosity by
    /// <paramref name="percentage"/> (a 0–1 fraction; defaults to 5%). The luminosity is clamped
    /// to <c>[0, 1]</c>, so darkening a near-black color saturates at black. <see cref="Alpha"/>
    /// is preserved. Adjusting luminosity is only meaningful for <see cref="ColorKind.Rgb"/>;
    /// palette and default colors are returned unchanged (matching <see cref="Lerp"/>/<see cref="Composite"/>).
    /// </summary>
    public Color Darken(double percentage = 0.05)
    {
        if (Kind != ColorKind.Rgb)
            return this;

        var (h, s, l) = ToHsl();
        return FromHsl(h, s, Math.Clamp(l - percentage, 0.0, 1.0), Alpha);
    }

    public override string ToString()
    {
        return ToString(null);
    }

    /// <summary>
    /// Compose <paramref name="source"/> over <paramref name="backdrop"/>: first apply the
    /// blending mode's color math, then composite the result against the backdrop linearly
    /// using the source's alpha. Returns an opaque color — the cell buffer always stores
    /// fully resolved colors because terminal output is fundamentally opaque.
    /// </summary>
    /// <remarks>
    /// Compositing is skipped (the mode's blended color is returned verbatim, normalized to
    /// alpha 255) when either operand isn't <see cref="ColorKind.Rgb"/>. The terminal default
    /// has no known RGB equivalent to mix against, and quantizing palette colors into RGB just
    /// to composite and back would be lossy and surprising. This matches how the built-in
    /// blending modes handle non-RGB inputs.
    /// </remarks>
    public static Color Composite(Color source, Color backdrop, IBlendingMode mode)
    {
        if (source.IsTransparent)
            return backdrop;

        if (backdrop.IsTransparent)
            return source;

        var blended = mode.Blend(source, backdrop);

        // Alpha compositing only engages for RGB-on-RGB. Otherwise, the source's alpha is
        // ignored, and the blended color (which is whatever the mode produced) wins outright.
        if (source.Kind != ColorKind.Rgb || backdrop.Kind != ColorKind.Rgb || source.Alpha == 255)
            return blended.Kind == ColorKind.Rgb ? blended.WithAlpha(255) : blended;

        int a = source.Alpha;
        int inv = 255 - a;

        return FromRgb(
            (byte) ((blended.Red * a + backdrop.Red * inv) / 255),
            (byte) ((blended.Green * a + backdrop.Green * inv) / 255),
            (byte) ((blended.Blue * a + backdrop.Blue * inv) / 255));
    }

    /// <summary>
    /// Compose <paramref name="source"/> over <paramref name="backdrop"/> the way <see cref="Composite"/>
    /// does, but <b>preserving the resulting alpha</b> instead of flattening it to 255 — Porter-Duff
    /// "over" in premultiplied sRGB, matching <see cref="Lerp"/>'s premultiplied convention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this when compositing into an <i>intermediate</i> surface that will itself be composited
    /// later. <see cref="Composite"/> is correct for the terminal's final target, where output is
    /// fundamentally opaque, but it reports every non-degenerate result as alpha 255; running two
    /// translucent colors through it turns them opaque, so a translucent element stacked inside an
    /// intermediate surface would lose its transparency before the surface is blended down.
    /// </para>
    /// <para>
    /// <b>Reduction identity:</b> when <paramref name="backdrop"/> is fully opaque this is bit-identical
    /// to <see cref="Composite"/>. The backdrop's contribution weight <c>ib</c> collapses to exactly
    /// <c>255 - source.Alpha</c>, the output alpha to exactly 255, and the channel expression to
    /// <see cref="Composite"/>'s. That makes adopting this at an already-opaque destination a provable
    /// no-op, so it is safe to route existing opaque paths through it.
    /// </para>
    /// <para>
    /// Non-RGB operands and the transparent short-circuits behave exactly as in <see cref="Composite"/>,
    /// for the same reasons.
    /// </para>
    /// </remarks>
    public static Color CompositeOver(Color source, Color backdrop, IBlendingMode mode)
    {
        if (source.IsTransparent)
            return backdrop;

        if (backdrop.IsTransparent)
            return source;

        // Every one of these lands on a fully opaque result, which is precisely what Composite
        // computes — so there is no alpha to preserve and no reason to pay for the general path.
        // (The opaque-backdrop case agrees with the general path exactly; see the reduction identity.)
        if (source.Kind != ColorKind.Rgb || backdrop.Kind != ColorKind.Rgb || source.Alpha == 255 || backdrop.Alpha == 255)
            return Composite(source, backdrop, mode);

        var blended = mode.Blend(source, backdrop);

        int a = source.Alpha;

        // The backdrop's share of the output, `backdrop.Alpha * (1 - a)`, in 0-255 units. Rounding
        // it once and reusing the same value in both the numerator and `ao` keeps the un-premultiply
        // consistent: the channel numerator is then bounded by 255 * ao, so no channel can exceed 255
        // and no clamp is needed. Computing `ao` from a truncated share but the channels from an
        // untruncated one would overflow a byte at low alphas (255/255 over 1/1 would land at 509).
        int ib = (backdrop.Alpha * (255 - a) + 127) / 255;
        int ao = a + ib;

        return FromRgba(
            (byte) ((blended.Red * a + backdrop.Red * ib) / ao),
            (byte) ((blended.Green * a + backdrop.Green * ib) / ao),
            (byte) ((blended.Blue * a + backdrop.Blue * ib) / ao),
            (byte) ao);
    }

    /// <summary>
    /// Interpolate from <paramref name="from"/> to <paramref name="to"/> at <paramref name="t"/> in
    /// <b>premultiplied sRGB</b> — the project-wide gradient/animation convention. RGB channels are
    /// premultiplied by alpha before blending and un-premultiplied afterward, so a fade toward a
    /// transparent endpoint keeps the opaque neighbor's hue and composes correctly through straight-alpha
    /// compositing. <paramref name="t"/> outside <c>[0, 1]</c> extrapolates (channels clamp to 0–255).
    /// </summary>
    /// <remarks>
    /// Channel blending is only meaningful for <see cref="ColorKind.Rgb"/>; if either endpoint is palette
    /// or default, this snaps to the nearer endpoint (<paramref name="from"/> when <paramref name="t"/>
    /// &lt; 0.5, else <paramref name="to"/>) — quantizing into RGB to blend would be lossy and surprising,
    /// matching <see cref="Composite"/>.
    /// </remarks>
    public static Color Lerp(Color from, Color to, double t)
    {
        if (from.Kind != ColorKind.Rgb || to.Kind != ColorKind.Rgb)
            return t < 0.5 ? from : to;

        double a0 = from.Alpha / 255.0;
        double a1 = to.Alpha / 255.0;

        double pr = LerpScalar(from.Red * a0, to.Red * a1, t);
        double pg = LerpScalar(from.Green * a0, to.Green * a1, t);
        double pb = LerpScalar(from.Blue * a0, to.Blue * a1, t);
        double a = LerpScalar(a0, a1, t);

        if (a <= LerpEpsilon)
            return FromRgba(0, 0, 0, ToByteClamped(a * 255.0));

        return FromRgba(ToByteClamped(pr / a), ToByteClamped(pg / a), ToByteClamped(pb / a), ToByteClamped(a * 255.0));
    }

    private const double LerpEpsilon = 1e-9;

    private static double LerpScalar(double a, double b, double t) => a + (b - a) * t;

    private static byte ToByteClamped(double value) => (byte) Math.Clamp(Math.Round(value), 0, 255);

    public string ToString(string? format, IFormatProvider? formatProvider = null)
    {
        FormattableString fs;

        if (format is null or "c" or "C" or "")
        {
            if (Kind is ColorKind.Rgb)
            {
                if (Alpha is not 255)
                    fs = $"rgba({Red},{Green},{Blue},{Alpha})";
                else
                    fs = $"rgb({Red},{Green},{Blue})";
            }
            else if (Kind is ColorKind.Palette)
            {
                if (Alpha is not 255)
                    fs = $"palette({PaletteIndex},a={Alpha})";
                else
                    fs = $"palette({PaletteIndex})";
            }
            else
            {
                fs = $"default";
            }
            
            return fs.ToString(formatProvider);
        }
        
        if (Kind is ColorKind.Rgb)
        {
            if (format is "X")
            {
                if (Alpha is not 255)
                    fs = $"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}";
                else
                    fs = $"#{Red:X2}{Green:X2}{Blue:X2}";

                return fs.ToString(formatProvider);
            }

            if (format is "x")
            {
                if (Alpha is not 255)
                    fs = $"#{Red:x2}{Green:x2}{Blue:x2}{Alpha:x2}";
                else
                    fs = $"#{Red:x2}{Green:x2}{Blue:x2}";

                return fs.ToString(formatProvider);
            }
        }

        throw new FormatException($"Invalid format for color kind {Kind}: '{format}'.");
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format,
                          IFormatProvider? provider = null)
    {
        var alpha = Alpha;

        if (format is "c" or "C" or "")
        {
            if (Kind is ColorKind.Rgb)
            {
                if (alpha is not 255)
                {
                    return destination.TryWrite(
                        provider,
                        $"rgba({Red},{Green},{Blue},{alpha})",
                        out charsWritten);
                }

                return destination.TryWrite(
                    provider,
                    $"rgb({Red},{Green},{Blue})",
                    out charsWritten);
            }

            if (Kind is ColorKind.Palette)
            {
                if (alpha is not 255)
                {
                    return destination.TryWrite(
                        provider,
                        $"palette({PaletteIndex},a={alpha})",
                        out charsWritten);
                }

                return destination.TryWrite(
                    provider,
                    $"palette({PaletteIndex})",
                    out charsWritten);
            }

            return destination.TryWrite(
                provider,
                $"default",
                out charsWritten);
        }

        if (Kind is ColorKind.Rgb)
        {
            if (format is "X")
            {
                if (alpha is not 255)
                {
                    return destination.TryWrite(
                        provider,
                        $"#{Red:X2}{Green:X2}{Blue:X2}{alpha:X2}",
                        out charsWritten);
                }

                return destination.TryWrite(
                    provider,
                    $"#{Red:X2}{Green:X2}{Blue:X2}",
                    out charsWritten);
            }

            if (format is "x")
            {
                if (alpha is not 255)
                {
                    return destination.TryWrite(
                        provider,
                        $"#{Red:x2}{Green:x2}{Blue:x2}{alpha:x2}",
                        out charsWritten);
                }

                return destination.TryWrite(
                    provider,
                    $"#{Red:x2}{Green:x2}{Blue:x2}",
                    out charsWritten);
            }
        }

        charsWritten = 0;
        return false;
    }
}

/// <summary>The representation a <see cref="Color"/> carries.</summary>
public enum ColorKind : byte
{
    /// <summary>Terminal default (no SGR color command needed for this slot).</summary>
    Default = 0,

    /// <summary>Indexed entry into the terminal's 256-color palette.</summary>
    Palette = 1,

    /// <summary>24-bit truecolor.</summary>
    Rgb = 2
}