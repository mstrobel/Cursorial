using Cursorial.Output;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Per-cell context handed to a <see cref="BrushedTextResolver"/> while <see cref="FormattedText.Paint"/>
/// walks a document. It carries <b>no brush</b> — a higher layer (e.g. <c>Cursorial.Drawing</c>) supplies the
/// resolver and owns any brush math; this only provides the cell's position and the rect of the element the
/// resolver should sample against. Keeping it brush-free is what lets the formatter stay in Rendering while
/// brushes live in Drawing (the §8 invariant: <c>IBrush</c> never enters <c>Style</c>).
/// </summary>
public readonly struct BrushedTextContext(Style baseStyle, int column, int row, Rect block, Rect run, object? tag)
{
    /// <summary>The cell's flat style — a resolver typically returns this with its colors swapped for brushed ones.</summary>
    public Style BaseStyle { get; } = baseStyle;

    /// <summary>The cell's column (buffer-local).</summary>
    public int Column { get; } = column;

    /// <summary>The cell's row (buffer-local).</summary>
    public int Row { get; } = row;

    /// <summary>The enclosing block's rect — the 2-D sampling bounds for a block/document-scoped brush.</summary>
    public Rect Block { get; } = block;

    /// <summary>
    /// The run's own rect on its line (its piece extent, 1 row) — the sampling bounds for an inline-scoped
    /// brush. For non-text elements the resolver isn't given a distinct run, so this equals <see cref="Block"/>.
    /// (A run that wraps is a per-line piece here; true reading-order wrap-invariance is a later refinement.)
    /// </summary>
    public Rect Run { get; } = run;

    /// <summary>
    /// The run's opaque <see cref="FormattedTextRun.Tag"/> (e.g. a Drawing <c>BrushedStyle</c>), or null. A
    /// resolver keys per-run brush selection off this.
    /// </summary>
    public object? Tag { get; } = tag;
}

/// <summary>
/// Resolves the effective <see cref="Style"/> for one painted text cell. A higher layer supplies it to
/// <see cref="FormattedText.Paint"/> to brush-color formatted text; the painter calls it per grapheme cell when
/// present. Cell width is grapheme-driven, so a substituted style never shifts the layout.
/// </summary>
public delegate Style BrushedTextResolver(in BrushedTextContext context);
