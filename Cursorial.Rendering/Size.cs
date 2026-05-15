using System.Text;

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

    public void Deconstruct(out int columns, out int rows)
    {
        columns = Columns;
        rows = Rows;
    }
    
    public static implicit operator Size((int Columns, int Rows) size) => new(size.Columns, size.Rows);

    public override string ToString()
    {
        return $"({Columns}×{Rows})";
    }
}
