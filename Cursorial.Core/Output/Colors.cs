namespace Cursorial.Output;

/// <summary>
/// Provides a collection of predefined colors for use in the application.
/// </summary>
/// <remarks>
/// This static class includes a variety of colors, ranging from basic palette-based colors
/// to explicitly defined RGB-based colors. It is meant to standardize color definitions
/// throughout the codebase.
/// </remarks>
public static class Colors
{
    /// <summary>
    /// Represents the default color used in the application.
    /// This value is equivalent to <see cref="Color.Default"/> and
    /// is used as a fallback or neutral color when no specific color is defined.
    /// </summary>
    public static readonly Color Default = Color.Default;

    /// <summary>
    /// Represents a transparent color with an alpha value of 0.
    /// This color is useful when rendering elements where transparency is required.
    /// </summary>
    public static readonly Color Transparent = Color.Transparent;

    /// <summary>
    /// Represents the pure black color, defined by RGB values of (0, 0, 0).
    /// This color is completely opaque and is not tied to any palette index.
    /// </summary>
    public static readonly Color TrueBlack = Color.FromRgb(0, 0, 0);

    /// <summary>
    /// Represents a color with an RGB value corresponding to pure white (255, 255, 255).
    /// This color is distinct from palette-based or default colors, ensuring an exact representation of white.
    /// </summary>
    public static readonly Color TrueWhite = Color.FromRgb(255, 255, 255);

    /// <summary>
    /// Represents the ANSI palette color entry for 'black', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (0) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Black = Color.FromPalette(0);

    /// <summary>
    /// Represents the ANSI palette color entry for 'red', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (1) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Red = Color.FromPalette(1);

    /// <summary>
    /// Represents the ANSI palette color entry for 'green', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (2) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Green = Color.FromPalette(2);

    /// <summary>
    /// Represents the ANSI palette color entry for 'yellow', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (3) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Yellow = Color.FromPalette(3);

    /// <summary>
    /// Represents the ANSI palette color entry for 'blue', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (4) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Blue = Color.FromPalette(4);

    /// <summary>
    /// Represents the ANSI palette color entry for 'magenta', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (5) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Magenta = Color.FromPalette(5);

    /// <summary>
    /// Represents the ANSI palette color entry for 'cyan', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (6) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Cyan = Color.FromPalette(6);

    /// <summary>
    /// Represents the ANSI palette color entry for 'white', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (7) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color White = Color.FromPalette(7);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light black', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (8) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightBlack = Color.FromPalette(8);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light red', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (9) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightRed = Color.FromPalette(9);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light green', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (10) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightGreen = Color.FromPalette(10);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light yellow', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (11) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightYellow = Color.FromPalette(11);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light blue', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (12) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightBlue = Color.FromPalette(12);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light magenta', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (13) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightMagenta = Color.FromPalette(13);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light cyan', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (14) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightCyan = Color.FromPalette(14);

    /// <summary>
    /// Represents the ANSI palette color entry for 'light white', though its actual appearance may be user-defined.
    /// This color is mapped to palette index (15) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color LightWhite = Color.FromPalette(15);

    /// <summary>
    /// Represents the first entry in the ANSI extended color palette.
    /// This color is mapped to palette index (16) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Extended1 = Color.FromPalette(16);


    /// <summary>
    /// Represents the second entry in the ANSI extended color palette.
    /// This color is mapped to palette index (17) through the static method <see cref="Color.FromPalette(byte)"/>,
    /// making it accessible through the <see cref="Color.PaletteIndex"/> property.
    /// </summary>
    public static readonly Color Extended2 = Color.FromPalette(17);
}