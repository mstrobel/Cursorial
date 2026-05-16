namespace Cursorial.Rendering;

/// <summary>
/// A rectangle in cell coordinates — anchor (top-left) plus a <see cref="Size"/>. Used by
/// dirty-region tracking, fragment-footprint reporting, and any other rendering-layer API that
/// needs to talk about "a rectangular cell region."
/// </summary>
/// <param name="Row">Top edge of the rectangle, 0-based. Inclusive.</param>
/// <param name="Column">Left edge of the rectangle, 0-based. Inclusive.</param>
/// <param name="Columns">Width in cells. Non-negative; 0 produces an empty rectangle.</param>
/// <param name="Rows">Height in cells. Non-negative; 0 produces an empty rectangle.</param>
public readonly record struct Rect(int Row, int Column, int Columns, int Rows)
{
    /// <summary>An empty rectangle anchored at (0, 0) with zero extent.</summary>
    public static Rect Empty => default;

    /// <summary>The exclusive bottom edge: <see cref="Row"/> + <see cref="Rows"/>.</summary>
    public int RowEnd => Row + Rows;

    /// <summary>The exclusive right edge: <see cref="Column"/> + <see cref="Columns"/>.</summary>
    public int ColumnEnd => Column + Columns;

    /// <summary>True when either dimension is zero.</summary>
    public bool IsEmpty => Columns == 0 || Rows == 0;

    /// <summary>True when the cell at (<paramref name="row"/>, <paramref name="column"/>) is inside the rectangle.</summary>
    public bool Contains(int row, int column)
        => row >= Row && row < RowEnd && column >= Column && column < ColumnEnd;
}
