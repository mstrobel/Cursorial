using Cursorial.Output;

namespace Cursorial.Rendering.Text;

/// <summary>
/// Per-cell context handed to a <see cref="BrushedTextResolver"/> while <see cref="FormattedText.Paint"/>
/// walks a document. It carries <b>no brush</b> — a higher layer (e.g. <c>Cursorial.Drawing</c>) supplies the
/// resolver and owns any brush math; this only provides the cell's position and the rect of the element the
/// resolver should sample against. Keeping it brush-free is what lets the formatter stay in Rendering while
/// brushes live in Drawing (the §8 invariant: <c>IBrush</c> never enters <c>Style</c>).
/// </summary>
public readonly struct BrushedTextContext(Style baseStyle, int column, int row, Rect block)
{
    /// <summary>The cell's flat style — a resolver typically returns this with its colors swapped for brushed ones.</summary>
    public Style BaseStyle { get; } = baseStyle;

    /// <summary>The cell's column (buffer-local).</summary>
    public int Column { get; } = column;

    /// <summary>The cell's row (buffer-local).</summary>
    public int Row { get; } = row;

    /// <summary>
    /// The enclosing block's rect — the 2-D sampling bounds for a block/document-scoped brush. (6a.2 will add
    /// the run's opaque tag and 1-D reading-order logical offset/width for inline-scoped, wrap-invariant sampling.)
    /// </summary>
    public Rect Block { get; } = block;
}

/// <summary>
/// Resolves the effective <see cref="Style"/> for one painted text cell. A higher layer supplies it to
/// <see cref="FormattedText.Paint"/> to brush-color formatted text; the painter calls it per grapheme cell when
/// present. Cell width is grapheme-driven, so a substituted style never shifts the layout.
/// </summary>
public delegate Style BrushedTextResolver(in BrushedTextContext context);
