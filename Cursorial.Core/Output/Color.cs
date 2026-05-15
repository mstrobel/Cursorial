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
/// Terminal output is always opaque: the cell buffer stores the composited result and the
/// renderer emits SGR codes for it as if it were a fully-opaque color.
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
    /// Alpha channel. 0 = fully transparent (source completely yields to backdrop during
    /// compositing); 255 = fully opaque (source replaces backdrop). Meaningful only for
    /// <see cref="ColorKind.Rgb"/> colors — non-RGB kinds short-circuit through compositing
    /// regardless of this value.
    /// </summary>
    public byte Alpha { get; }
    
    public uint RgbaValue => (uint)Red << 16 | (uint)Green << 8 | Blue;

    /// <summary>True when this color requests the terminal's default for its slot.</summary>
    public bool IsDefault => Kind == ColorKind.Default;

    /// <summary>True when the color is fully opaque (alpha = 255) or its kind ignores alpha.</summary>
    public bool IsOpaque => Kind != ColorKind.Rgb || Alpha == 255;

    /// <summary>Construct a palette color from a 0–255 index. Indices 0–15 are the ANSI base colors.</summary>
    public static Color FromPalette(byte index)
    {
        return new Color(ColorKind.Palette, index, 0, 0, 255);
    }

    /// <summary>Construct a fully-opaque 24-bit truecolor value.</summary>
    public static Color FromRgb(byte red, byte green, byte blue)
    {
        return new Color(ColorKind.Rgb, red, green, blue, 255);
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