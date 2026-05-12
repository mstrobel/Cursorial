namespace Cursorial.Core.Output;

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
    private readonly byte _byte0; // R or palette index, depending on kind.
    private readonly byte _byte1; // G.
    private readonly byte _byte2; // B.
    private readonly byte _alpha; // Alpha (0–255). Meaningful only for ColorKind.Rgb.

    /// <summary>The representation of this color.</summary>
    public ColorKind Kind { get; }

    private Color(ColorKind kind, byte b0, byte b1, byte b2, byte alpha)
    {
        Kind = kind;
        _byte0 = b0;
        _byte1 = b1;
        _byte2 = b2;
        _alpha = alpha;
    }

    /// <summary>
    /// The terminal's default color for the slot this is assigned to (foreground or background).
    /// <see cref="Alpha"/> is held at 0 to keep this value equivalent to <c>default(Color)</c>;
    /// alpha is not applied to <see cref="ColorKind.Default"/> colors anyway.
    /// </summary>
    public static Color Default { get; } = new(ColorKind.Default, 0, 0, 0, 0);

    /// <summary>Construct a palette color from a 0–255 index. Indices 0–15 are the ANSI base colors.</summary>
    public static Color FromPalette(byte index) => new(ColorKind.Palette, index, 0, 0, 255);

    /// <summary>Construct a fully-opaque 24-bit truecolor value.</summary>
    public static Color FromRgb(byte red, byte green, byte blue) => new(ColorKind.Rgb, red, green, blue, 255);

    /// <summary>
    /// Construct a 24-bit truecolor value with an explicit alpha channel. <paramref name="alpha"/>
    /// of 255 is fully opaque (equivalent to <see cref="FromRgb"/>); 0 is fully transparent
    /// (compositing returns the backdrop unchanged). Intermediate values mix the blended source
    /// color with the backdrop linearly.
    /// </summary>
    public static Color FromRgba(byte red, byte green, byte blue, byte alpha)
        => new(ColorKind.Rgb, red, green, blue, alpha);

    /// <summary>
    /// The palette index. Defined only when <see cref="Kind"/> is <see cref="ColorKind.Palette"/>;
    /// reads as 0 for other kinds (callers should guard on <see cref="Kind"/>).
    /// </summary>
    public byte PaletteIndex => _byte0;

    /// <summary>Red component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Red => _byte0;

    /// <summary>Green component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Green => _byte1;

    /// <summary>Blue component for <see cref="ColorKind.Rgb"/> colors; 0 otherwise.</summary>
    public byte Blue => _byte2;

    /// <summary>
    /// Alpha channel. 0 = fully transparent (source completely yields to backdrop during
    /// compositing); 255 = fully opaque (source replaces backdrop). Meaningful only for
    /// <see cref="ColorKind.Rgb"/> colors — non-RGB kinds short-circuit through compositing
    /// regardless of this value.
    /// </summary>
    public byte Alpha => _alpha;

    /// <summary>True when this color requests the terminal's default for its slot.</summary>
    public bool IsDefault => Kind == ColorKind.Default;

    /// <summary>True when the color is fully opaque (alpha = 255) or its kind ignores alpha.</summary>
    public bool IsOpaque => Kind != ColorKind.Rgb || _alpha == 255;

    /// <summary>
    /// Return a copy of this color with <see cref="Alpha"/> set to <paramref name="alpha"/>.
    /// A no-op for <see cref="ColorKind.Default"/> (alpha is meaningless there).
    /// </summary>
    public Color WithAlpha(byte alpha)
    {
        if (Kind == ColorKind.Default) return this;
        return new(Kind, _byte0, _byte1, _byte2, alpha);
    }

    public override string ToString() => Kind switch
    {
        ColorKind.Default => "default",
        ColorKind.Palette => _alpha == 255 ? $"palette({_byte0})" : $"palette({_byte0},a={_alpha})",
        ColorKind.Rgb => _alpha == 255
            ? $"rgb({_byte0},{_byte1},{_byte2})"
            : $"rgba({_byte0},{_byte1},{_byte2},{_alpha})",
        _ => "<invalid>",
    };
}

/// <summary>The representation a <see cref="Color"/> carries.</summary>
public enum ColorKind : byte
{
    /// <summary>Terminal default (no SGR color command needed for this slot).</summary>
    Default = 0,

    /// <summary>Indexed entry into the terminal's 256-color palette.</summary>
    Palette = 1,

    /// <summary>24-bit truecolor.</summary>
    Rgb = 2,
}
