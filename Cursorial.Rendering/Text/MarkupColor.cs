using System.Globalization;

using Cursorial.Media;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Parses a markup color token — the syntax accepted by <c>[fg=…]</c> / <c>[bg=…]</c>, reused by brush-markup
/// grammars: a <c>#rgb</c> / <c>#rrggbb</c> hex literal, a palette index <c>0–255</c>, or a named color
/// (<c>red</c>, <c>brightblue</c>, <c>gray</c>, …). Public so a higher layer (e.g. Drawing's gradient markup)
/// can parse the same color tokens without duplicating the named-color table.
/// </summary>
public static class MarkupColor
{
    private static readonly Dictionary<string, byte> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0, ["red"] = 1, ["green"] = 2, ["yellow"] = 3,
        ["blue"] = 4, ["magenta"] = 5, ["cyan"] = 6, ["white"] = 7,
        ["brightblack"] = 8, ["gray"] = 8, ["grey"] = 8,
        ["brightred"] = 9, ["brightgreen"] = 10, ["brightyellow"] = 11,
        ["brightblue"] = 12, ["brightmagenta"] = 13, ["brightcyan"] = 14,
        ["brightwhite"] = 15,
    };

    /// <summary>Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="color"/> = default) for an unrecognized token.</summary>
    public static bool TryParse(string? value, out Color color) => TryParse(value, out color, out _);

    /// <summary>Try to parse a color token (#hex, palette index 0–255, or a named color). Case-insensitive
    /// for names; returns false (and <paramref name="color"/> = default) for an unrecognized token.</summary>
    public static bool TryParse(string? value, out Color color, out Exception? error)
    {
        color = default;
        error = null;

        if (string.IsNullOrEmpty(value)) return false;

        if (value.StartsWith('#')) return Color.TryParseHex(value.AsSpan(1), out color, out error);

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            if (index is < 0 or > 255) return false;
            color = Color.FromPalette((byte) index);
            return true;
        }

        if (NamedColors.TryGetValue(value, out byte palette))
        {
            color = Color.FromPalette(palette);
            return true;
        }

        return false;
    }

    /// <summary>Parse a color token, throwing <see cref="FormatException"/> on an unrecognized one.</summary>
    public static Color Parse(string value) =>
        TryParse(value, out var color, out var error)
            ? color
            : throw new FormatException($"Unrecognized color '{value}'. Use a name, palette index 0–255, or #rgb / #rrggbb hex.", error);
}
