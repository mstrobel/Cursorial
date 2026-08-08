using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Cursorial.Media;
using Cursorial.Rendering.Media;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Parses a markup color token — the syntax accepted by <c>[fg=…]</c> / <c>[bg=…]</c>, reused by brush-markup
/// grammars: a <c>#rgb</c> / <c>#rrggbb[aa]</c> hex literal, a palette index <c>0–255</c>, or a named color
/// (<c>red</c>, <c>brightblue</c>, <c>gray</c>, …). Public so a higher layer (e.g. Drawing's gradient markup)
/// can parse the same color tokens without duplicating the named-color table.
/// </summary>
public static class MarkupColor
{
    private static readonly Dictionary<string, byte> NamedColors;

    static MarkupColor()
    {
        NamedColors = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
                      {
                          ["black"] = 0, ["red"] = 1, ["green"] = 2, ["yellow"] = 3,
                          ["blue"] = 4, ["magenta"] = 5, ["cyan"] = 6, ["white"] = 7,
                          ["brightblack"] = 8, ["gray"] = 8, ["grey"] = 8,
                          ["brightred"] = 9, ["brightgreen"] = 10, ["brightyellow"] = 11,
                          ["brightblue"] = 12, ["brightmagenta"] = 13, ["brightcyan"] = 14,
                          ["brightwhite"] = 15, ["lightred"] = 9, ["lightgreen"] = 10,
                          ["lightyellow"] = 11, ["lightblue"] = 12, ["lightmagenta"] = 13,
                          ["lightcyan"] = 14, ["lightwhite"] = 15 
                      };
    }

    /// <summary>
    /// Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="color"/> = <c>default</c>) for an unrecognized token.
    /// </summary>
    public static bool TryParse(string? value, out Color color) => TryParse(value, out color, out _);

    /// <summary>
    /// Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="color"/> = default) for an unrecognized token.
    /// </summary>
    public static bool TryParse(string? value, out Color color, out Exception? error)
    {
        if (TryParseCore(value, out color, out error) is false) return false;
        
        if (color is { Kind: ColorKind.Palette, PaletteIndex: var index })
            color = index < 16 ? Color.FromPalette(index) : ColorPalette.Ansi256[index];
        
        return true;
    }

    private static bool TryParseCore(string? value, out Color color, out Exception? error)
    {
        color = default;
        error = null;

        if (string.IsNullOrEmpty(value)) return false;

        if (value.StartsWith('#')) return Color.TryParseHex(value.AsSpan(1), out color, out error);

        if (char.IsAsciiDigit(value[0]) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
        {
            if (index is < 0 or > 255) return false;
            color = Color.FromPalette((byte)index);
            return true;
        }

        if (NamedColors.TryGetValue(value, out var palette))
        {
            color = palette < 16 ? Color.FromPalette(palette) : ColorPalette.Ansi256[palette];
            return true;
        }

        return false;
    }

    /// <summary>Parse a color token, throwing <see cref="FormatException"/> on an unrecognized one.</summary>
    public static Color Parse(string value) =>
        TryParse(value, out var color, out var error)
            ? color
            : throw new FormatException($"Unrecognized color '{value}'. Use a name, palette index 0–255, or #rgb / #rrggbb[aa] hex.", error);

    /// <summary>
    /// Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="brush"/> = <c>null</c>) for an unrecognized token.
    /// </summary>
    public static bool TryParseBrush(string? value,[NotNullWhen(true)] out IBrush? brush) => TryParseBrush(value, out brush, out _);

    /// <summary>Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="brush"/> = default) for an unrecognized token.</summary>
    public static bool TryParseBrush(string? value, [NotNullWhen(true)] out IBrush? brush, out Exception? error)
    {
        brush = null;

        if (TryParseCore(value, out var color, out error) is false) return false;
        
        if (color is { Kind: ColorKind.Palette, PaletteIndex: var index })
            color = index < 16 ? Color.FromPalette(index) : ColorPalette.Ansi256[index];
        
        return true;
    }

    /// <summary>Parse a color token, throwing <see cref="FormatException"/> on an unrecognized one.</summary>
    public static IBrush ParseBrush(string value) =>
        TryParseBrush(value, out var brush, out var error)
            ? brush
            : throw new FormatException($"Unrecognized color '{value}'. Use a name, palette index 0–255, or #rgb / #rrggbb[aa] hex.", error);
}
