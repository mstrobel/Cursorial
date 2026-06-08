namespace Cursorial.Drawing;

/// <summary>
/// Dash density on a straight (2-arm) run, named by dash count. Applies only to straight light/heavy
/// runs — dropped at corners, junctions, caps, on double lines (Unicode has no dashed glyph there), and
/// at a line's lone end cells (a 1-arm end renders the solid line / stub, not a dash).
/// </summary>
public enum LineDash : byte
{
    /// <summary>Solid line (─│). The default.</summary>
    None = 0,

    /// <summary>Two dashes (╌╎ / ╍╏).</summary>
    Double = 2,

    /// <summary>Three dashes (┄┆ / ┅┇).</summary>
    Triple = 3,

    /// <summary>Four dashes (┈┊ / ┉┋).</summary>
    Quadruple = 4
}
