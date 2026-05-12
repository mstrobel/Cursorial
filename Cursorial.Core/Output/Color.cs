namespace Cursorial.Core.Output;

/// <summary>
/// A terminal color in one of three representations: the terminal's <see cref="ColorKind.Default"/>
/// foreground/background, an indexed entry into the 256-color <see cref="ColorKind.Palette"/>,
/// or a 24-bit <see cref="ColorKind.Rgb"/> truecolor value.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Color"/> is a value type with no allocation: the discriminator and the three
/// component bytes fit in a single 32-bit field. Two <see cref="Color"/> values compare equal
/// when their kind and payload match.
/// </para>
/// <para>
/// Use the static factories rather than the parameterized constructor: <see cref="Default"/>,
/// <see cref="FromPalette"/>, and <see cref="FromRgb"/>. The default-constructed
/// <see cref="Color"/> is equivalent to <see cref="Default"/>.
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

    /// <summary>The representation of this color.</summary>
    public ColorKind Kind { get; }

    private Color(ColorKind kind, byte b0, byte b1, byte b2)
    {
        Kind = kind;
        _byte0 = b0;
        _byte1 = b1;
        _byte2 = b2;
    }

    /// <summary>The terminal's default color for the slot this is assigned to (foreground or background).</summary>
    public static Color Default { get; } = new(ColorKind.Default, 0, 0, 0);

    /// <summary>Construct a palette color from a 0–255 index. Indices 0–15 are the ANSI base colors.</summary>
    public static Color FromPalette(byte index) => new(ColorKind.Palette, index, 0, 0);

    /// <summary>Construct a 24-bit truecolor value.</summary>
    public static Color FromRgb(byte red, byte green, byte blue) => new(ColorKind.Rgb, red, green, blue);

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

    /// <summary>True when this color requests the terminal's default for its slot.</summary>
    public bool IsDefault => Kind == ColorKind.Default;

    public override string ToString() => Kind switch
    {
        ColorKind.Default => "default",
        ColorKind.Palette => $"palette({_byte0})",
        ColorKind.Rgb => $"rgb({_byte0},{_byte1},{_byte2})",
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
