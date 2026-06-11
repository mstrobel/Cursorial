// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Resolves a braille dot mask (one bit per 2×4 sub-cell dot) to a glyph in the Braille Patterns block
/// (U+2800–28FF, width-1). The dot→bit mapping is the standard Unicode layout, verified against the
/// <c>unicodedata</c> oracle (and test-pinned): column 0 top→bottom is dots 1,2,3,7; column 1 is dots
/// 4,5,6,8 — i.e. <c>bit(dx,dy) = dy&lt;3 ? dx*3+dy : 6+dx</c>. Glyphs are interned (zero per-cell alloc).
/// </summary>
internal static class BrailleGlyphs
{
    private static readonly string[] Table = Build();

    private static string[] Build()
    {
        var table = new string[256];
        for (int mask = 0; mask < 256; mask++)
            table[mask] = ((char) (0x2800 + mask)).ToString();
        return table;
    }

    /// <summary>The bit index (0–7) of the dot at within-cell position (<paramref name="dx"/> 0–1, <paramref name="dy"/> 0–3).</summary>
    public static int Bit(int dx, int dy) => dy < 3 ? dx * 3 + dy : 6 + dx;

    /// <summary>The glyph for the 8-dot <paramref name="mask"/>.</summary>
    public static string Glyph(byte mask, GlyphSet set = GlyphSet.Unicode)
    {
        if (set == GlyphSet.Ascii) return mask == 0 ? " " : "*";
        return Table[mask];
    }
}
