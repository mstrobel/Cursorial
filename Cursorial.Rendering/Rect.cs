using System.Runtime.CompilerServices;

using Cursorial.Input;

namespace Cursorial.Rendering;

/// <summary>
/// A rectangle in cell coordinates — anchor (top-left) plus a <see cref="Size"/>. Used by
/// dirty-region tracking, fragment-footprint reporting, and any other rendering-layer API that
/// needs to talk about "a rectangular cell region."
/// </summary>
public readonly record struct Rect
{
    /// <summary>
    /// The maximum value of any <see cref="Rect"/> dimension or coordinate (<see cref="Column"/>/<see cref="Row"/>/
    /// <see cref="Columns"/>/<see cref="Rows"/>). The <see cref="Rect"/> is <see cref="int"/>-backed, so this is the
    /// full non-negative range. NOTE: <c>edge + extent</c> properties (<see cref="RowEnd"/>/<see cref="ColumnEnd"/>)
    /// can overflow <see cref="int"/> for a hand-built <see cref="Rect"/> whose coordinate AND extent are both near
    /// the cap — callers own that arithmetic. The LAYOUT clamp (<c>LayoutMath.MaxExtent</c>) is a separate, lower
    /// ceiling that keeps every layout-produced <see cref="Rect"/> safely within range.
    /// </summary>
    public const int MaxDimension = int.MaxValue;

    /// <summary>
    /// Creates a rectangle from the cell coordinates of its top-left corner and its dimensions
    /// (width and height).
    /// </summary>
    /// <param name="row">Top edge of the rectangle, 0-based. Inclusive.</param>
    /// <param name="column">Left edge of the rectangle, 0-based. Inclusive.</param>
    /// <param name="columns">Width in cells. Non-negative; 0 produces an empty rectangle.</param>
    /// <param name="rows">Height in cells. Non-negative; 0 produces an empty rectangle.</param>
    public Rect(int column, int row, int columns, int rows)
    {
        if (columns < 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Rectangle anchor coordinates cannot be negative.");

        if (rows < 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Rectangle anchor coordinates cannot be negative.");

        Column = column;
        Row = row;
        Columns = columns;
        Rows = rows;
    }

    /// <summary>
    /// Creates a rectangle from the cell coordinates of its top-left corner and its dimensions
    /// (width and height).
    /// </summary>
    /// <param name="column">Top edge of the rectangle, 0-based. Inclusive.</param>
    /// <param name="row">Left edge of the rectangle, 0-based. Inclusive.</param>
    /// <param name="size">Dimensions of the rectangle, measured in cells.</param>
    public Rect(int column, int row, Size size) : this(column, row, size.Columns, size.Rows) {}

    /// <summary>
    /// Creates a rectangle from at cell coordinates (0, 0) with the given dimensions
    /// (width and height).
    /// </summary>
    /// <param name="size">Dimensions of the rectangle, measured in cells.</param>
    public Rect(Size size) : this(0, 0, size.Columns, size.Rows) {}

    /// <summary>
    /// A rectangle in cell coordinates — anchor (top-left) plus a <see cref="Size"/>. Used by
    /// dirty-region tracking, fragment-footprint reporting, and any other rendering-layer API that
    /// needs to talk about "a rectangular cell region."
    /// </summary>
    /// <param name="position">The anchor position (top-left corner) of the rectangle.</param>
    /// <param name="size">Dimensions of the rectangle, measured in cells.</param>
    public Rect(CellPosition position, Size size) : this(position.Column, position.Row, size) {}

    /// <summary>An empty rectangle anchored at (0, 0) with zero extent.</summary>
    public static Rect Empty => default;

    /// <summary>The exclusive bottom edge: <see cref="Row"/> + <see cref="Rows"/>.</summary>
    public int RowEnd => Row + Rows;

    /// <summary>The exclusive right edge: <see cref="Column"/> + <see cref="Columns"/>.</summary>
    public int ColumnEnd => Column + Columns;

    /// <summary>True when either dimension is zero.</summary>
    public bool IsEmpty => Columns == 0 || Rows == 0;

    /// <summary>The rectangle's anchor position.</summary>
    public CellPosition Position => new(Column, Row);

    /// <summary>The rectangle's dimensions as a <see cref="Size"/> (dropping the anchor position).</summary>
    public Size Size => new(Columns, Rows);

    /// <summary>Left edge of the rectangle, 0-based. Inclusive.</summary>
    public int Column
    {
        get;
        init => field = ValidateDimension(in value);
    }

    /// <summary>Top edge of the rectangle, 0-based. Inclusive.</summary>
    public int Row
    {
        get;
        init => field = ValidateDimension(in value);
    }

    /// <summary>Width in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Columns
    {
        get;
        init => field = ValidateDimension(in value);
    }

    /// <summary>Height in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Rows
    {
        get;
        init => field = ValidateDimension(in value);
    }

    /// <summary>The cell position of the top-left corner of the rectangle.</summary>
    public CellPosition TopLeft => new(Column, Row);

    /// <summary>The cell position of the top-right corner of the rectangle.</summary>
    public CellPosition TopRight => new(ColumnEnd - 1, Row);

    /// <summary>The cell position of the bottom-left corner of the rectangle.</summary>
    public CellPosition BottomLeft => new(Column, RowEnd - 1);

    /// <summary>The cell position of the bottom-right corner of the rectangle.</summary>
    public CellPosition BottomRight => new(ColumnEnd - 1, RowEnd - 1);

    /// <summary>True when the cell at <paramref name="position"/> is inside the rectangle.</summary>
    public bool Contains(CellPosition position)
        => position.Row >= Row &&
           position.Row < RowEnd &&
           position.Column >= Column &&
           position.Column < ColumnEnd;

    public bool Contains(int column, int row)
        => row >= Row && row < RowEnd && column >= Column && column < ColumnEnd;

    /// <summary>True when this rectangle intersects with <paramref name="other"/>.</summary>
    public bool Intersects(Rect other)
        => Row < other.RowEnd && RowEnd > other.Row && Column < other.ColumnEnd && ColumnEnd > other.Column;

    /// <summary>True when this rectangle fully contains <paramref name="other"/>.</summary>
    public bool Contains(Rect other)
        => Contains(other.Column, other.Row) && Contains(other.ColumnEnd - 1, other.RowEnd - 1);

    /// <summary>Returns the intersection of this rectangle with <paramref name="other"/> —
    /// <see cref="Empty"/> when they do not overlap.</summary>
    public Rect Intersection(Rect other)
    {
        int column = Math.Max(Column, other.Column);
        int row = Math.Max(Row, other.Row);
        int columnEnd = Math.Min(ColumnEnd, other.ColumnEnd);
        int rowEnd = Math.Min(RowEnd, other.RowEnd);
        return columnEnd > column && rowEnd > row
            ? new Rect(column, row, columnEnd - column, rowEnd - row)
            : Empty;
    }


    /// <summary>
    /// Deconstructs the rectangle into its top-left corner coordinates and dimensions.
    /// </summary>
    /// <param name="column">The column index of the top-left corner of the rectangle.</param>
    /// <param name="row">The row index of the top-left corner of the rectangle.</param>
    /// <param name="columns">The width of the rectangle in cells.</param>
    /// <param name="rows">The height of the rectangle in cells.</param>
    public void Deconstruct(out int column, out int row, out int columns, out int rows)
    {
        column = Column;
        row = Row;
        columns = Columns;
        rows = Rows;
    }

    /// <inheritdoc/>
    public override string ToString() => $"Rect({Size} @ {Position})";

    /// <summary>
    /// Creates a rectangle positioned based on an anchor point, size, and optional margins.
    /// </summary>
    /// <param name="anchor">
    /// Defines the anchor point relative to which the rectangle is positioned.
    /// </param>
    /// <param name="size">
    /// Specifies the width (columns) and height (rows) of the rectangle.
    /// </param>
    /// <param name="margins">
    /// Optional margins specifying the amount of space to exclude from each side of the available area. Defaults to no margins.
    /// </param>
    /// <returns>
    /// A new rectangle positioned within the available area according to the specified anchor, size, and margins.
    /// </returns>
    public Rect LayoutContent(Anchor anchor, Size size, Margins margins = default)
    {
        var availableColumns = Columns - margins.Left - margins.Right;
        var availableRows = Rows - margins.Top - margins.Bottom;

        var column = anchor switch
                     {
                         Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft    => Column + margins.Left,
                         Anchor.Top or Anchor.Center or Anchor.Bottom          => Column + margins.Left + (availableColumns - size.Columns) / 2,
                         Anchor.TopRight or Anchor.Right or Anchor.BottomRight => Column + margins.Left + availableColumns - size.Columns,
                         _                                                     => Column + margins.Left
                     };

        var row = anchor switch
                  {
                      Anchor.TopLeft or Anchor.Top or Anchor.TopRight          => Row + margins.Top,
                      Anchor.Left or Anchor.Center or Anchor.Right             => Row + margins.Top + (availableRows - size.Rows) / 2,
                      Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => Row + margins.Top + availableRows - size.Rows,
                      _                                                        => Row + margins.Top
                  };

        return new Rect(column, row, size);
    }

    /// <summary>
    /// Calculates and returns the position of the content's anchor point within a rectangular region,
    /// based on the specified anchor, size, and margins.
    /// </summary>
    /// <param name="anchor">Defines the anchor point of the content within the rectangular region.</param>
    /// <param name="size">The size of the content in terms of columns and rows.</param>
    /// <param name="margins">Optional margins applied to the rectangular region, used to adjust the layout.</param>
    /// <returns>
    /// A <see cref="CellPosition"/> representing the calculated position of the anchor point within the
    /// rectangular region.
    /// </returns>
    public CellPosition AnchorContent(Anchor anchor, Size size, Margins margins = default)
        => LayoutContent(anchor, size, margins).Position;

    /// <summary>
    /// Creates a new rectangle with the same top-left corner and the specified dimensions.
    /// </summary>
    /// <param name="size">The dimensions (width and height) to apply to the new rectangle.</param>
    /// <return>A rectangle with the current top-left corner and the specified dimensions.</return>
    public Rect WithSize(Size size) => new(Column, Row, size);

    /// <summary>
    /// Creates a new rectangle with the same top-left corner as the current rectangle but with the
    /// specified dimensions.
    /// </summary>
    /// <param name="columns">The new width of the rectangle in cells. Must be non-negative.</param>
    /// <param name="rows">The new height of the rectangle in cells. Must be non-negative.</param>
    /// <returns>A new <see cref="Rect"/> instance with the updated dimensions and the same position.</returns>
    public Rect WithSize(int columns, int rows) => new(Column, Row, columns, rows);
    
    /// <summary>
    /// Creates a new rectangle with the same dimensions as the current rectangle but with
    /// the top-left corner translated by the specified offsets.
    /// </summary>
    /// <param name="offsetColumn">The relative horizontal offset in columns.</param>
    /// <param name="offsetRow">The relative vertical offset in rows.</param>
    /// <returns>A new <see cref="Rect"/> instance with the updated position and the same dimensions.</returns>
    public Rect Translate(int offsetColumn, int offsetRow)
        => new(Column + offsetColumn, Row + offsetRow, Columns, Rows);
    
    internal static int ValidateDimension(in int value, [CallerMemberName] string? propertyName = "dimensions")
    {
        if (value is >= 0 and <= MaxDimension)
            return value;

        throw new ArgumentOutOfRangeException(
            propertyName,
            value,
            $"Rect {propertyName} must be between 0 and {MaxDimension:N0}.");
    }
}