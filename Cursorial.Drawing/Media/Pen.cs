using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Drawing.Media;

/// <summary>
/// A stroke definition for line / box drawing: a color source (<see cref="Brush"/>) plus the
/// glyph-selecting stroke attributes. Color is kept <b>separate</b> from stroke attributes (unlike a
/// bundled "line brush"). A terminal stroke is always one cell wide — <see cref="Weight"/> selects a
/// glyph family, never pixel thickness.
/// </summary>
/// <remarks>
/// <c>default(Pen)</c> is a usable pen — light, sharp, solid, un-capped, <see cref="JunctionMode.Merge"/>,
/// Unicode — whose null <see cref="Brush"/> resolves to the terminal's default foreground at draw time,
/// so <c>ctx.DrawBox(rect, default)</c> draws a plain light border. There is no implicit
/// <see cref="Color"/> → <see cref="Pen"/> conversion (an interface can't be a conversion target and the
/// implicit form misbinds overloads); every <see cref="Pen"/>-typed draw method ships a sibling
/// <see cref="Color"/> overload instead.
/// </remarks>
public readonly record struct Pen
{
    /// <summary>Create a solid pen over <paramref name="color"/>.</summary>
    public Pen(Color color) : this(new SolidColorBrush(color)) { }

    /// <summary>Create a pen over <paramref name="brush"/>; null means the terminal's default foreground.</summary>
    public Pen(IBrush? brush) => Brush = brush;

    /// <summary>
    /// The color source. Null (the default) resolves to <see cref="Colors.Default"/> at draw time. A
    /// gradient brush samples against the drawn figure's bounds.
    /// </summary>
    public IBrush? Brush { get; init; }

    /// <summary>The line family (default <see cref="StrokeWeight.Light"/>).</summary>
    public StrokeWeight Weight { get; init; }

    /// <summary>Corner treatment (default <see cref="CornerStyle.Sharp"/>).</summary>
    public CornerStyle Corners { get; init; }

    /// <summary>Dash density on straight runs (default <see cref="LineDash.None"/>).</summary>
    public LineDash Dash { get; init; }

    /// <summary>How a lone open end is drawn (default <see cref="EndCap.None"/>).</summary>
    public EndCap EndCap { get; init; }

    /// <summary>How this stroke crosses other strokes (default <see cref="JunctionMode.Merge"/>).</summary>
    public JunctionMode Junction { get; init; }

    /// <summary>Glyph repertoire (default <see cref="GlyphSet.Unicode"/>).</summary>
    public GlyphSet GlyphSet { get; init; }

    /// <summary>
    /// Non-color SGR styling carried onto each stroked cell (bold / faint / blink …) — the stroke's
    /// equivalent of <see cref="CellStyle.Attributes"/>. Color still comes from <see cref="Brush"/>.
    /// </summary>
    public TextAttributes Attributes { get; init; }

    /// <summary>Return a copy with a different <see cref="Brush"/>.</summary>
    public Pen WithBrush(IBrush? brush) => this with { Brush = brush };

    /// <summary>Return a copy whose <see cref="Brush"/> is a solid <paramref name="color"/>.</summary>
    public Pen WithColor(Color color) => this with { Brush = new SolidColorBrush(color) };

    /// <summary>Return a copy with a different <see cref="Weight"/>.</summary>
    public Pen WithWeight(StrokeWeight weight) => this with { Weight = weight };

    /// <summary>Return a copy with a different <see cref="Corners"/>.</summary>
    public Pen WithCorners(CornerStyle corners) => this with { Corners = corners };

    /// <summary>Return a copy with a different <see cref="Dash"/>.</summary>
    public Pen WithDash(LineDash dash) => this with { Dash = dash };

    /// <summary>Return a copy with a different <see cref="EndCap"/>.</summary>
    public Pen WithEndCap(EndCap endCap) => this with { EndCap = endCap };

    /// <summary>Return a copy with a different <see cref="Junction"/>.</summary>
    public Pen WithJunction(JunctionMode junction) => this with { Junction = junction };

    /// <summary>Return a copy with a different <see cref="GlyphSet"/>.</summary>
    public Pen WithGlyphSet(GlyphSet glyphSet) => this with { GlyphSet = glyphSet };

    /// <summary>Return a copy with different <see cref="Attributes"/>.</summary>
    public Pen WithAttributes(TextAttributes attributes) => this with { Attributes = attributes };

    /// <summary>The brush to paint with — never null (null <see cref="Brush"/> → <see cref="Brushes.Default"/>).</summary>
    internal IBrush ResolveBrush() => Brush ?? Brushes.Default;
}
