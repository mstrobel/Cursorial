using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// The signed arranged-bounds carrier (matrix LD19): a rectangle in cell coordinates whose origin
/// may be <b>negative</b> (signed margins can pull a child above/left of its parent — design doc
/// §5.2) and whose size is non-negative, validated into <c>[0, <see cref="LayoutMath.MaxExtent"/>]</c>.
/// <see cref="UIElement.Bounds"/> is typed as this; Rendering's ushort-backed <see cref="Rect"/> is
/// unchanged and remains the vocabulary for slots, dirty regions, and draw calls. A <see cref="Rect"/>
/// converts implicitly (widening); <see cref="ToRect"/> is the explicit narrowing affordance for
/// callers that have proven a non-negative origin (no consumer is wired today — render and hit
/// paths stay signed end-to-end).
/// </summary>
public readonly record struct LayoutRect
{
    private readonly int _columns;
    private readonly int _rows;

    /// <summary>
    /// Creates a rectangle from the (possibly negative) cell coordinates of its top-left corner
    /// and its non-negative dimensions.
    /// </summary>
    /// <param name="column">Left edge, 0-based, inclusive. May be negative.</param>
    /// <param name="row">Top edge, 0-based, inclusive. May be negative.</param>
    /// <param name="columns">Width in cells, in <c>[0, <see cref="LayoutMath.MaxExtent"/>]</c>; 0 produces an empty rectangle.</param>
    /// <param name="rows">Height in cells, in <c>[0, <see cref="LayoutMath.MaxExtent"/>]</c>; 0 produces an empty rectangle.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is negative or exceeds <see cref="LayoutMath.MaxExtent"/>.</exception>
    public LayoutRect(int column, int row, int columns, int rows)
    {
        Column = column;
        Row = row;
        _columns = ValidateExtent(columns, nameof(columns));
        _rows = ValidateExtent(rows, nameof(rows));
    }

    /// <summary>Creates a rectangle from a (possibly negative) top-left corner and a size.</summary>
    public LayoutRect(int column, int row, Size size) : this(column, row, size.Columns, size.Rows) {}

    /// <summary>An empty rectangle anchored at (0, 0) with zero extent.</summary>
    public static LayoutRect Empty => default;

    /// <summary>Left edge of the rectangle, 0-based, inclusive. May be negative (LD19).</summary>
    public int Column { get; init; }

    /// <summary>Top edge of the rectangle, 0-based, inclusive. May be negative (LD19).</summary>
    public int Row { get; init; }

    /// <summary>Width in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Columns
    {
        get => _columns;
        init => _columns = ValidateExtent(value, nameof(Columns));
    }

    /// <summary>Height in cells. Non-negative; 0 produces an empty rectangle.</summary>
    public int Rows
    {
        get => _rows;
        init => _rows = ValidateExtent(value, nameof(Rows));
    }

    /// <summary>The exclusive right edge: <see cref="Column"/> + <see cref="Columns"/>.</summary>
    public int ColumnEnd => Column + _columns;

    /// <summary>The exclusive bottom edge: <see cref="Row"/> + <see cref="Rows"/>.</summary>
    public int RowEnd => Row + _rows;

    /// <summary>True when either dimension is zero.</summary>
    public bool IsEmpty => _columns == 0 || _rows == 0;

    /// <summary>The rectangle's dimensions as a <see cref="Size"/> (dropping the anchor position).</summary>
    public Size Size => new(_columns, _rows);

    /// <summary>True when the cell at (<paramref name="column"/>, <paramref name="row"/>) is inside the rectangle.</summary>
    public bool Contains(int column, int row)
        => row >= Row && row < RowEnd && column >= Column && column < ColumnEnd;

    /// <summary>
    /// Converts to Rendering's ushort-backed <see cref="Rect"/> — the explicit narrowing
    /// affordance for callers that have proven a non-negative origin (kept as API surface; the
    /// render/hit paths consume the signed form directly).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The origin is negative (a signed-margin placement; see LD19).</exception>
    public Rect ToRect() => new(Column, Row, _columns, _rows);

    /// <summary>Widening conversion — every <see cref="Rect"/> is a valid <see cref="LayoutRect"/>.</summary>
    public static implicit operator LayoutRect(Rect rect) => new(rect.Column, rect.Row, rect.Columns, rect.Rows);

    /// <inheritdoc/>
    public override string ToString() => $"LayoutRect({_columns}x{_rows} @ ({Column}, {Row}))";

    private static int ValidateExtent(int value, string parameterName)
    {
        if (value is >= 0 and <= LayoutMath.MaxExtent)
            return value;

        throw new ArgumentOutOfRangeException(
            parameterName, value, $"LayoutRect dimensions must be between 0 and {LayoutMath.MaxExtent:N0}.");
    }
}
