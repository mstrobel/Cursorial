namespace Cursorial.Output;

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
public readonly record struct Color
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
        var span = hexCode;
        
        // Remove leading '#' if present
        if (span.Length > 0 && span[0] == '#')
            span = span[1..];
        
        // Support 3-digit (#RGB) and 6-digit (#RRGGBB) hex codes
        if (span.Length == 3)
        {
            // Convert 3-digit format to 6-digit by doubling each digit
            byte r = (byte)(ParseHexDigit(span[0]) * 17);
            byte g = (byte)(ParseHexDigit(span[1]) * 17);
            byte b = (byte)(ParseHexDigit(span[2]) * 17);
            return FromRgb(r, g, b);
        }

        if (span.Length == 6)
        {
            byte r = (byte)((ParseHexDigit(span[0]) << 4) | ParseHexDigit(span[1]));
            byte g = (byte)((ParseHexDigit(span[2]) << 4) | ParseHexDigit(span[3]));
            byte b = (byte)((ParseHexDigit(span[4]) << 4) | ParseHexDigit(span[5]));
            return FromRgb(r, g, b);
        }

        throw new ArgumentException($"Invalid hex code format. Expected 3 or 6 hex digits, got {span.Length}.", nameof(hexCode));

        static int ParseHexDigit(char c)
        {
            if (c is >= '0' and <= '9')
                return c - '0';
            if (c is >= 'a' and <= 'f')
                return c - 'a' + 10;
            if (c is >= 'A' and <= 'F')
                return c - 'A' + 10;
            throw new ArgumentException($"Invalid hex digit: '{c}'");
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
    /// Return a copy of this color with <see cref="Alpha"/> set to <paramref name="alpha"/>.
    /// A no-op for <see cref="ColorKind.Default"/> (alpha is meaningless there).
    /// </summary>
    public Color WithAlpha(byte alpha)
    {
        if (Kind == ColorKind.Default) return this;
        return new Color(Kind, PaletteIndex, Green, Blue, alpha);
    }

    public override string ToString()
    {
        return Kind switch
               {
                   ColorKind.Default => "default",
                   ColorKind.Palette => Alpha == 255 ? $"palette({PaletteIndex})" : $"palette({PaletteIndex},a={Alpha})",
                   ColorKind.Rgb     => Alpha == 255 ? $"rgb({PaletteIndex},{Green},{Blue})" : $"rgba({PaletteIndex},{Green},{Blue},{Alpha})",
                   _                 => "<invalid>"
               };
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