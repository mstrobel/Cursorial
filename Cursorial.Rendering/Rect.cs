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
    private readonly ushort _column;
    private readonly ushort _row;
    private readonly ushort _columns;
    private readonly ushort _rows;

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

        _column = (ushort) column;
        _row = (ushort) row;
        _columns = (ushort) columns;
        _rows = (ushort) rows;

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
        get => _column;
        init => _column = ValidateDimension(in value);
    }

    /// <summary>Top edge of the rectangle, 0-based. Inclusive.</summary>
    public int Row
    {
        get => _row;
        init => _row = ValidateDimension(in value);
    }

    /// <summary>Width in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Columns
    {
        get => _columns;
        init => _columns = ValidateDimension(in value);
    }

    /// <summary>Height in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Rows
    {
        get => _rows;
        init => _rows = ValidateDimension(in value);
    }

    /// <summary>True when the cell at (<paramref name="row"/>, <paramref name="column"/>) is inside the rectangle.</summary>
    public bool Contains(int column, int row)
        => row >= Row && row < RowEnd && column >= Column && column < ColumnEnd;

    /// <summary>True when this rectangle intersects with <paramref name="other"/>.</summary>
    public bool Intersects(Rect other)
        => Row < other.RowEnd && RowEnd > other.Row && Column < other.ColumnEnd && ColumnEnd > other.Column;

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
    
    private ushort ValidateDimension(in int value, [CallerMemberName] string? propertyName = "dimensions")
    {
        if (value is >= 0 and <= ushort.MaxValue)
            return (ushort) value;

        throw new ArgumentOutOfRangeException(
            propertyName,
            value,
            $"Rectangle {propertyName} must be between 0 and {ushort.MaxValue:N0}.");
    }
}