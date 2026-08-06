using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.Drawing.Media;

/// <summary>
/// Brush-sampling bounds for a <b>deferred</b> stroke / braille record: a rectangle in final scene
/// coordinates whose origin is <em>signed</em>. Deferred records capture the ambient translate at
/// record time, and a negative translate (scrolled content) legitimately places a record's sampling
/// rect partly above/left of the scene — which the <c>ushort</c>-backed <see cref="Rect"/> can't
/// carry. <see cref="Sample"/> bridges back to the <see cref="IBrush.ColorAt"/> contract: when the
/// origin is non-negative it passes the rect through verbatim; otherwise it shifts both the sample
/// point and the rect to a zero origin — exactly equivalent under the contract's bounds-relative
/// sampling rule (<c>column − bounds.Column + 0.5</c>).
/// </summary>
internal readonly record struct SampleBounds(int Column, int Row, int Columns, int Rows)
{
    /// <summary>The exclusive right edge.</summary>
    public int ColumnEnd => Column + Columns;

    /// <summary>The exclusive bottom edge.</summary>
    public int RowEnd => Row + Rows;

    /// <summary>A local-space rect translated into scene space by the ambient translate captured at record time.</summary>
    public static SampleBounds From(in Rect rect, int translateColumns, int translateRows)
        => new(rect.Column + translateColumns, rect.Row + translateRows, rect.Columns, rect.Rows);

    /// <summary>Sample <paramref name="brush"/> at the scene cell (<paramref name="column"/>, <paramref name="row"/>).</summary>
    public Color Sample(IBrush brush, int column, int row)
    {
        // Sizes can exceed the Rect cap only through a pathological figure union; clamp defensively
        // (the gradient parameter compresses slightly in that case rather than throwing).
        int columns = Math.Min(Columns, ushort.MaxValue);
        int rows = Math.Min(Rows, ushort.MaxValue);

        return (uint) Column <= ushort.MaxValue && (uint) Row <= ushort.MaxValue
                   ? brush.ColorAt(column, row, new Rect(Column, Row, columns, rows))
                   : brush.ColorAt(column - Column, row - Row, new Rect(0, 0, columns, rows));
    }

    /// <summary>The smallest bounds containing both this and <paramref name="other"/> (the figure back-patch union).</summary>
    public SampleBounds Union(in SampleBounds other)
    {
        int column = Math.Min(Column, other.Column);
        int row = Math.Min(Row, other.Row);
        int columnEnd = Math.Max(ColumnEnd, other.ColumnEnd);
        int rowEnd = Math.Max(RowEnd, other.RowEnd);
        return new SampleBounds(column, row, columnEnd - column, rowEnd - row);
    }
}
