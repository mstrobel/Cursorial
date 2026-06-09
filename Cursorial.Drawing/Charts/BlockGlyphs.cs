namespace Cursorial.Drawing;

/// <summary>Which edge a fractional block bar fills from.</summary>
internal enum BlockAxis
{
    /// <summary>Fills upward from the bottom (lower-eighth blocks ▁▂▃▄▅▆▇█).</summary>
    Vertical,

    /// <summary>Fills rightward from the left (left-eighth blocks ▏▎▍▌▋▊▉█).</summary>
    Horizontal
}

/// <summary>
/// Resolves a fractional fill level (0–8 eighths) to a Block-Elements glyph. The two ramps are the only
/// <b>complete</b> 8-step block families in Unicode — the lower (U+2581–2588) and left (U+258F–2588)
/// ramps; the upper/right families are sparse, which is why <see cref="BlockAxis"/> offers only these
/// two orientations. Codepoints verified against the <c>unicodedata</c> oracle (and pinned by a test).
/// Stateless and never-throws: each bar/sparkline cell is written exactly once, so no accumulator.
/// </summary>
internal static class BlockGlyphs
{
    //               level: 0     1     2     3     4     5     6     7     8
    private static readonly string[] LowerRamp = [" ", "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];
    private static readonly string[] LeftRamp  = [" ", "▏", "▎", "▍", "▌", "▋", "▊", "▉", "█"];

    // Far-edge partials (top for Vertical, right for Horizontal). Unlike the lower/left ramps these are NOT
    // complete in Unicode — only a one-eighth sliver (▔ / ▕) and a half block (▀ / ▐) exist — so a fill that
    // grows toward the far edge (e.g. a negative bar below a zero baseline) is quantized to {0, ⅛, ½, full}.
    private static readonly string[] UpperRamp = [" ", "▔", "▔", "▀", "▀", "▀", "▀", "█", "█"];
    private static readonly string[] RightRamp = [" ", "▕", "▕", "▐", "▐", "▐", "▐", "█", "█"];

    /// <summary>Quantize a fraction (0–1) to an eighth level 0–8.</summary>
    public static int Level(double fraction) =>
        (int) Math.Round(Math.Clamp(fraction, 0.0, 1.0) * 8.0);

    /// <summary>The glyph for <paramref name="level"/> (clamped 0–8) along <paramref name="axis"/>, filled
    /// from the NEAR edge (bottom for Vertical, left for Horizontal). The complete 8-step ramps.</summary>
    public static string Glyph(int level, BlockAxis axis, GlyphSet set = GlyphSet.Unicode)
    {
        level = Math.Clamp(level, 0, 8);
        if (set == GlyphSet.Ascii)
            return level == 0 ? " " : level == 8 ? "#" : "+";
        return axis == BlockAxis.Vertical ? LowerRamp[level] : LeftRamp[level];
    }

    /// <summary>The glyph for <paramref name="level"/> (clamped 0–8) filled from the FAR edge (top for
    /// Vertical, right for Horizontal) — coarser than <see cref="Glyph"/>, as Unicode's far ramps are
    /// sparse (only ⅛ and ½ partials exist). Used for the negative direction of a signed, baseline-anchored bar.</summary>
    public static string FarGlyph(int level, BlockAxis axis, GlyphSet set = GlyphSet.Unicode)
    {
        level = Math.Clamp(level, 0, 8);
        if (set == GlyphSet.Ascii)
            return level == 0 ? " " : level == 8 ? "#" : "+";
        return axis == BlockAxis.Vertical ? UpperRamp[level] : RightRamp[level];
    }
}
