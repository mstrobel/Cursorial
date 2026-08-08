using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Drawing.Media;

/// <summary>
/// One deferred stroke draw call's resolved state, accumulated during a scene draw pass and consumed
/// at flush. <see cref="Bounds"/> starts as the call's own shape rect (translated into scene
/// coordinates by the ambient push translate captured at record time) and is back-patched to the
/// figure's union (or explicit) bounds when a figure closes; the implicit root never closes, so root
/// records keep per-call bounds. <see cref="FigureId"/> partitions junctions (strokes only merge
/// within the same figure).
/// </summary>
internal struct StrokeRecord
{
    /// <summary>The resolved brush (never null — a null pen brush became <see cref="Brushes.Default"/>).</summary>
    public IBrush Brush;

    /// <summary>
    /// The brush sampling bounds in scene coordinates (per-call, or figure union/explicit after back-patch).
    /// The origin is <em>signed</em>: a negative ambient translate (scrolled content) legitimately places a
    /// record's sampling rect partly above/left of the scene, and <see cref="IBrush.ColorAt"/> reads the
    /// origin only as a subtrahend, so a negative one samples exactly as the shifted-to-zero equivalent
    /// (pinned by <c>BrushNegativeOriginEquivalenceTests</c>).
    /// </summary>
    public Rect Bounds;

    /// <summary>Topology-independent decoration (corners / dash / cap).</summary>
    public StrokeDecoration Decoration;

    /// <summary>Glyph repertoire (Unicode / ASCII).</summary>
    public GlyphSet GlyphSet;

    /// <summary>Non-color SGR styling carried onto each stroked cell.</summary>
    public TextAttributes Attributes;

    /// <summary>When true, this stroke's cells overwrite existing glyphs (bypass text-beats-decoration eviction).</summary>
    public bool Overwrite;

    /// <summary>The figure this stroke belongs to (0 = implicit root); set by the accumulator.</summary>
    public int FigureId;
}
