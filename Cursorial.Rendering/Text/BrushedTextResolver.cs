using Cursorial.Output;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Per-RUN context handed to a <see cref="BrushedTextResolver"/> while <see cref="FormattedText.Paint"/> walks
/// a document. It carries <b>no brush of the resolver's own</b> — a higher layer (e.g. <c>Cursorial.Drawing</c>)
/// supplies the resolver and owns any brush math; this provides only what the resolver needs to CHOOSE one:
/// the run's own carrier, the enclosing block's declared foreground, and the two candidate sampling boxes the
/// painter has already worked out.
/// </summary>
/// <remarks>
/// It used to be per-CELL, carrying the cell's column/row and its logical offset within the inline scope,
/// because the resolver had to return a fully sampled colour. It now returns a
/// <see cref="BrushedStyle"/> — the brush itself, unsampled — so sampling moved to the painter (and,
/// for a glyph face, into the font), and every per-cell member became the painter's own arithmetic. What
/// remains is genuinely per-run: which declaration wins, and in which coordinate space it samples.
/// </remarks>
public readonly struct BrushedTextContext(BrushedStyle style, IBrush? blockForeground, Rect block, Rect inlineScope, UnderlineStyle underlineShape)
{
    /// <summary>
    /// The run's own <see cref="BrushedStyle"/> carrier, unresolved. The channels it STATES are the run's own
    /// declarations — a resolver reads its <see cref="BrushedStyle.Foreground"/> first when deciding which
    /// brush wins. Scope is inferred from the declaration site: a brush stated on the run samples
    /// <see cref="InlineScope"/>.
    /// </summary>
    public BrushedStyle Style { get; } = style;

    /// <summary>
    /// The enclosing block's declared foreground brush, or null when the block states none. The block rung of
    /// the ladder: it loses to a run's own declaration and beats the document default and the caller's
    /// preference. A block declaration samples <see cref="Block"/>.
    /// </summary>
    public IBrush? BlockForeground { get; } = blockForeground;

    /// <summary>The enclosing block's rect — the 2-D sampling bounds for a block-declared brush. This is
    /// the walk's ink-anchored EXTENT: where the block's cells land under the block's own alignment, so a
    /// block gradient ramps across the glyphs rather than across a copy of the box pinned to the paint
    /// rect's left edge.</summary>
    public Rect Block { get; } = block;

    /// <summary>
    /// The run's wrap-invariant reading-order strip: a 1-row rect whose <see cref="Rect.Column"/> is the
    /// scene column at which the source run's logical offset 0 would sit, and whose width is the run's total
    /// logical extent. Sampling a brush at the CELL's own <c>(column, row)</c> against this rect therefore
    /// yields the cell's logical offset within the run — continuous across a wrap, instead of restarting per
    /// line-piece — with no second coordinate convention for the painter to keep straight.
    /// </summary>
    /// <remarks>
    /// This is the geometry that used to be spelled <c>ColorAt(LogicalColumn, 0, Rect(0, 0, ScopeWidth, 1))</c>
    /// from a per-cell context. Expressing it as a REBASED RECT rather than a remapped coordinate is what lets
    /// the inline and block scopes share one sampling call, which is in turn what lets the resolver hand back a
    /// bounds rect instead of a sampled colour.
    /// </remarks>
    public Rect InlineScope { get; } = inlineScope;

    /// <summary>
    /// The run's RESOLVED underline shape — what the painter's fall-through fold produced for this run,
    /// whichever rung stated it. The element-attribute leg re-states it when the Underline presence bit
    /// merges (the flag has no "on, and no further opinion" form of its own — see
    /// <see cref="BrushedStyle.Imposing"/>), so an imposed underline never clobbers a run's authored shape.
    /// </summary>
    public UnderlineStyle UnderlineShape { get; } = underlineShape;
}

/// <summary>
/// A style delta to paint a run with, and the coordinate space its brushes sample against. The pair is one
/// value because neither half means anything alone: the same gradient over a run's own strip and over the
/// enclosing block are different pictures.
/// </summary>
/// <param name="Style">What the brushed document does to the run's style — <c>default</c> for "nothing".</param>
/// <param name="Bounds">The rect <paramref name="Style"/>'s brushes are sampled against.</param>
public readonly record struct BrushedTextStyle(BrushedStyle Style, Rect Bounds)
{
    /// <summary>The identity: no channel claimed, so the run paints exactly as it was formatted.</summary>
    public static BrushedTextStyle None => default;

    /// <summary>Resolve the delta for one cell — <see cref="BrushedStyle.Resolve(int,int,in Rect)"/>
    /// against <see cref="Bounds"/>, so no caller has to keep the two halves paired by hand.</summary>
    public PartialStyle Resolve(int column, int row) => Style.Resolve(column, row, Bounds);

    /// <summary>Resolve for one cell and fold onto <paramref name="baseStyle"/>.</summary>
    public CellStyle ApplyTo(int column, int row, in CellStyle baseStyle) => Resolve(column, row).ApplyTo(baseStyle);
}

/// <summary>
/// Chooses the style delta for one painted RUN. A higher layer supplies it to
/// <see cref="FormattedText.Paint"/> to brush-color formatted text; the painter calls it once per run and
/// resolves the returned style per cell. Cell width is grapheme-driven, so a substituted style never
/// shifts the layout.
/// </summary>
/// <remarks>
/// <para>
/// Returning a <see cref="BrushedStyle"/> rather than a whole <see cref="CellStyle"/> is what lets a
/// resolver state only the channels it owns. A brush owns a foreground; before the change it had to hand back
/// a reconstruction of the run's resolved style with that one channel swapped, so every
/// channel it forgot was silently reset and "this run keeps what it had" cost a full copy per cell instead of
/// being the identity.
/// </para>
/// <para>
/// Returning it UNSAMPLED is what moved the call from per-cell to per-run. Everything the resolver decides —
/// which declaration wins, at what scope, which inherited attributes merge in — is a property of the run;
/// only the sampling was per cell, and the style does that itself.
/// It also lets a glyph face receive the brush rather than a blind callback, so a face that paints a
/// multi-cell glyph per character can sample per CELL (the FIGlet gradient) while a uniform style is
/// resolved once for the whole run.
/// </para>
/// </remarks>
public delegate BrushedTextStyle BrushedTextResolver(in BrushedTextContext context);
