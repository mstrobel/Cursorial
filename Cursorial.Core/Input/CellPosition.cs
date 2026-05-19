namespace Cursorial.Input;

/// <summary>
/// A position within the terminal grid, reported in cell coordinates and optionally pixel
/// coordinates. Used by mouse, pen, touch, and other pointer-style events. Negative values may
/// be reported when the pointer is dragged outside the terminal viewport.
/// </summary>
/// <param name="Column">Zero-based column (cell).</param>
/// <param name="Row">Zero-based row (cell).</param>
/// <param name="PixelX">Pixel X coordinate when reported by the terminal; null otherwise.</param>
/// <param name="PixelY">Pixel Y coordinate when reported by the terminal; null otherwise.</param>
public readonly record struct CellPosition(int Column, int Row, int? PixelX = null, int? PixelY = null)
{
    /// <summary>
    /// Indicates whether the terminal has reported pixel coordinates for the specified position.
    /// This property returns true if both the <c>PixelX</c> and <c>PixelY</c> coordinates
    /// have non-null values; otherwise, it returns false.
    /// </summary>
    public bool HasPixelCoordinates => PixelX.HasValue && PixelY.HasValue;

    /// <summary>
    /// Defines an implicit conversion from a tuple containing column and row values to a <see cref="CellPosition"/>.
    /// This allows for seamless creation of a <see cref="CellPosition"/> instance from a tuple with an integer
    /// column and row without the need for an explicit constructor.
    /// </summary>
    /// <param name="tuple">A tuple containing two integers representing the column and row of the cell position.</param>
    /// <returns>A new <see cref="CellPosition"/> instance initialized with the column and row values from the provided tuple.</returns>
    public static implicit operator CellPosition((int Column, int Row) tuple) => new(tuple.Column, tuple.Row);
}