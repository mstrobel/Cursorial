namespace Cursorial.Drawing.Media;

/// <summary>
/// Which glyph repertoire a stroke renders with. A <em>consumer</em> choice (not a terminal-capability
/// one — the drawing layer can't see terminal capabilities; those resolve later at the renderer).
/// </summary>
public enum GlyphSet : byte
{
    /// <summary>Unicode box-drawing glyphs (─│┌┼━╋═╬…). The default.</summary>
    Unicode = 0,

    /// <summary>Plain ASCII (<c>- | +</c>) — for dumb terminals or copy-paste-safe output.</summary>
    Ascii = 1
}
