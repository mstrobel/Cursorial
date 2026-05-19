namespace Cursorial.Rendering;

/// <summary>
/// Discrete 2D size in cell units — used by font measurement, fragment bounds, and any
/// rendering-layer API that needs to talk about "how many cells wide × how many cells tall."
/// </summary>
/// <param name="Columns">Width in cells. Non-negative.</param>
/// <param name="Rows">Height in cells. Non-negative.</param>
public readonly record struct Size(int Columns, int Rows)
{
    /// <summary>An empty size (0 × 0).</summary>
    public static Size Empty => default;

    /// <summary>True when both dimensions are zero.</summary>
    public bool IsEmpty => Columns == 0 && Rows == 0;

    /// <summary>
    /// Deconstructs the current Size instance into its individual components: columns and rows.
    /// </summary>
    /// <param name="columns">The number of columns represented by the Size instance.</param>
    /// <param name="rows">The number of rows represented by the Size instance.</param>
    public void Deconstruct(out int columns, out int rows)
    {
        columns = Columns;
        rows = Rows;
    }

    /// <summary>
    /// Defines an implicit conversion from a tuple of integers to a <see cref="Size"/> instance.
    /// </summary>
    /// <param name="size">A tuple containing the number of columns and rows representing the size.</param>
    /// <returns>A new <see cref="Size"/> instance with the specified columns and rows.</returns>
    public static implicit operator Size((int Columns, int Rows) size) => new(size.Columns, size.Rows);

    /// <summary>
    /// Returns a string representation of the Size, formatted as "(Columns×Rows)".
    /// </summary>
    /// <returns>A string representing the dimensions of the Size instance in terms of columns and rows.</returns>
    public override string ToString()
    {
        return $"({Columns}×{Rows})";
    }

    /// <summary>
    /// Increases the size of the current <see cref="Size"/> instance by the specified margins.
    /// </summary>
    /// <param name="margins">The margins to be added, represented as horizontal and vertical measurements.</param>
    /// <returns>
    /// A new <see cref="Size"/> instance with updated dimensions, where the columns are increased by the horizontal margin
    /// and rows are increased by the vertical margin.
    /// </returns>
    public Size GrowBy(Margins margins) => new(Columns + margins.Horizontal, Rows + margins.Vertical);
}