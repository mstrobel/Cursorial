namespace Cursorial.Drawing;

/// <summary>
/// Resolves an accumulated arm code (+ decoration) to a box-drawing glyph. The base/rounded/dash/cap
/// tables were <b>generated from the Unicode box-drawing block (U+2500–257F) via the official character
/// names</b> (an offline <c>unicodedata</c> oracle), so the mixed light/heavy codepoints (┍ vs ┎, …)
/// are correct by construction, not by recall. <c>BoxGlyphsTests</c> pins the mixed-junction / fallback
/// codepoints to guard against drift.
/// </summary>
/// <remarks>
/// Arm code: a byte, 2 bits per direction — Up = bits 0–1, Right = 2–3, Down = 4–5, Left = 6–7; each
/// field is 0 = none / 1 = light / 2 = heavy / 3 = double. All returned glyphs are interned string
/// literals (no per-cell allocation). Resolution is a fallback ladder that never throws.
/// </remarks>
internal static class BoxGlyphs
{
    private const string Space = " ";

    // Straight-run line glyphs by weight (index 0 unused / none, 1 light, 2 heavy, 3 double). Used for
    // a lone arm with no cap, and when a dash is dropped.
    private static readonly string[] HLine = [Space, "─", "━", "═"];
    private static readonly string[] VLine = [Space, "│", "┃", "║"];

    // Exact base glyphs (>= 2 arms, plus the pure lines), indexed by arm code; null = no exact glyph.
    private static readonly string?[] Base = BuildTable(
    [
        (0x05, "└"), (0x06, "┖"), (0x07, "╙"), (0x09, "┕"), (0x0A, "┗"), (0x0D, "╘"), (0x0F, "╚"),
        (0x11, "│"), (0x12, "╿"), (0x14, "┌"), (0x15, "├"), (0x16, "┞"), (0x18, "┍"), (0x19, "┝"),
        (0x1A, "┡"), (0x1C, "╒"), (0x1D, "╞"), (0x21, "╽"), (0x22, "┃"), (0x24, "┎"), (0x25, "┟"),
        (0x26, "┠"), (0x28, "┏"), (0x29, "┢"), (0x2A, "┣"), (0x33, "║"), (0x34, "╓"), (0x37, "╟"),
        (0x3C, "╔"), (0x3F, "╠"), (0x41, "┘"), (0x42, "┚"), (0x43, "╜"), (0x44, "─"), (0x45, "┴"),
        (0x46, "┸"), (0x47, "╨"), (0x48, "╼"), (0x49, "┶"), (0x4A, "┺"), (0x50, "┐"), (0x51, "┤"),
        (0x52, "┦"), (0x54, "┬"), (0x55, "┼"), (0x56, "╀"), (0x58, "┮"), (0x59, "┾"), (0x5A, "╄"),
        (0x60, "┒"), (0x61, "┧"), (0x62, "┨"), (0x64, "┰"), (0x65, "╁"), (0x66, "╂"), (0x68, "┲"),
        (0x69, "╆"), (0x6A, "╊"), (0x70, "╖"), (0x73, "╢"), (0x74, "╥"), (0x77, "╫"), (0x81, "┙"),
        (0x82, "┛"), (0x84, "╾"), (0x85, "┵"), (0x86, "┹"), (0x88, "━"), (0x89, "┷"), (0x8A, "┻"),
        (0x90, "┑"), (0x91, "┥"), (0x92, "┩"), (0x94, "┭"), (0x95, "┽"), (0x96, "╃"), (0x98, "┯"),
        (0x99, "┿"), (0x9A, "╇"), (0xA0, "┓"), (0xA1, "┪"), (0xA2, "┫"), (0xA4, "┱"), (0xA5, "╅"),
        (0xA6, "╉"), (0xA8, "┳"), (0xA9, "╈"), (0xAA, "╋"), (0xC1, "╛"), (0xC3, "╝"), (0xCC, "═"),
        (0xCD, "╧"), (0xCF, "╩"), (0xD0, "╕"), (0xD1, "╡"), (0xDC, "╤"), (0xDD, "╪"), (0xF0, "╗"),
        (0xF3, "╣"), (0xFC, "╦"), (0xFF, "╬"),
    ]);

    // The four all-light corners that have a rounded arc variant.
    private static readonly string?[] Rounded = BuildTable(
    [
        (0x05, "╰"), (0x14, "╭"), (0x41, "╯"), (0x50, "╮"),
    ]);

    // Half-stub glyphs for a lone-arm cell with EndCap.Stub, indexed by arm code (light + heavy only).
    private static readonly string?[] Cap = BuildTable(
    [
        (0x01, "╵"), (0x02, "╹"),   // up   light / heavy
        (0x04, "╶"), (0x08, "╺"),   // right
        (0x10, "╷"), (0x20, "╻"),   // down
        (0x40, "╴"), (0x80, "╸"),   // left
    ]);

    private static string?[] BuildTable(ReadOnlySpan<(byte Code, string Glyph)> seed)
    {
        var table = new string?[256];
        foreach (var (code, glyph) in seed)
            table[code] = glyph;
        return table;
    }

    /// <summary>Resolve <paramref name="arms"/> + <paramref name="deco"/> to a glyph (never throws).</summary>
    public static string Resolve(byte arms, in StrokeDecoration deco, GlyphSet set)
    {
        if (set == GlyphSet.Ascii)
            return AsciiFor(arms);

        int count = ArmCount(arms);
        if (count == 0)
            return Space;

        if (count == 1)
        {
            if (deco.Cap == EndCap.Stub && Cap[arms] is { } stub)
                return stub;
            return LoneLine(arms);   // no cap → the full line, not a stub
        }

        if (count == 2)
        {
            if (deco.Dash != LineDash.None && TryStraightRun(arms, out bool vertical, out int runWeight)
                && DashFor(vertical, runWeight, deco.Dash) is { } dashed)
                return dashed;

            if (deco.Corners == CornerStyle.Rounded && Rounded[arms] is { } arc)
                return arc;
        }

        return Base[arms] ?? Fallback(arms);
    }

    // ---- fallback ladder: heavy+double mixes (no Unicode glyph) downgrade, then collapse to light ----
    private static string Fallback(byte arms)
    {
        if (Base[MapWeights(arms, static w => w == 3 ? 2 : w)] is { } downgraded)
            return downgraded;
        if (Base[MapWeights(arms, static w => w == 0 ? 0 : 1)] is { } light)
            return light;
        return AsciiFor(arms);
    }

    // ---- helpers ----
    internal static int WeightOf(byte arms, int dir) => (arms >> (dir * 2)) & 3;

    private static int ArmCount(byte arms) =>
        (WeightOf(arms, 0) != 0 ? 1 : 0) + (WeightOf(arms, 1) != 0 ? 1 : 0) +
        (WeightOf(arms, 2) != 0 ? 1 : 0) + (WeightOf(arms, 3) != 0 ? 1 : 0);

    private static string LoneLine(byte arms)
    {
        int up = WeightOf(arms, 0), right = WeightOf(arms, 1), down = WeightOf(arms, 2), left = WeightOf(arms, 3);
        if (left != 0) return HLine[left];
        if (right != 0) return HLine[right];
        if (up != 0) return VLine[up];
        return VLine[down];
    }

    private static bool TryStraightRun(byte arms, out bool vertical, out int weight)
    {
        int up = WeightOf(arms, 0), right = WeightOf(arms, 1), down = WeightOf(arms, 2), left = WeightOf(arms, 3);
        if (left != 0 && left == right && up == 0 && down == 0) { vertical = false; weight = left; return true; }
        if (up != 0 && up == down && left == 0 && right == 0) { vertical = true; weight = up; return true; }
        vertical = false; weight = 0; return false;
    }

    private static string? DashFor(bool vertical, int weight, LineDash dash) => (vertical, weight, dash) switch
    {
        (false, 1, LineDash.Double) => "╌", (false, 1, LineDash.Triple) => "┄", (false, 1, LineDash.Quadruple) => "┈",
        (false, 2, LineDash.Double) => "╍", (false, 2, LineDash.Triple) => "┅", (false, 2, LineDash.Quadruple) => "┉",
        (true, 1, LineDash.Double) => "╎", (true, 1, LineDash.Triple) => "┆", (true, 1, LineDash.Quadruple) => "┊",
        (true, 2, LineDash.Double) => "╏", (true, 2, LineDash.Triple) => "┇", (true, 2, LineDash.Quadruple) => "┋",
        _ => null,
    };

    private static byte MapWeights(byte arms, Func<int, int> map)
    {
        int result = 0;
        for (int dir = 0; dir < 4; dir++)
            result |= (map(WeightOf(arms, dir)) & 3) << (dir * 2);
        return (byte) result;
    }

    private static string AsciiFor(byte arms)
    {
        bool horizontal = WeightOf(arms, 1) != 0 || WeightOf(arms, 3) != 0;
        bool vertical = WeightOf(arms, 0) != 0 || WeightOf(arms, 2) != 0;
        if (horizontal && vertical) return "+";
        if (horizontal) return "-";
        if (vertical) return "|";
        return Space;
    }
}
