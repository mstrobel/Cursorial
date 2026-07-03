namespace Cursorial.Drawing.Media;

/// <summary>
/// The box-drawing line family a stroke renders in. Selects a <b>glyph family</b>, never pixel
/// thickness — a terminal stroke is always one cell wide. Distinct from <see cref="LineDash"/>: a
/// heavy dashed line is heavy <em>weight</em> + dash <em>density</em>, two independent axes.
/// </summary>
public enum StrokeWeight : byte
{
    /// <summary>Light single line (─│┌┼). The default.</summary>
    Light = 0,

    /// <summary>Heavy single line (━┃┏╋).</summary>
    Heavy = 1,

    /// <summary>Double line (═║╔╬).</summary>
    Double = 2
}
