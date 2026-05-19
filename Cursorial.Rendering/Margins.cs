namespace Cursorial.Rendering;

/// <summary>
/// Represents the desired margins of a content element, defining the measurements
/// for the left, top, right, and bottom sides.
/// </summary>
public readonly record struct Margins
{
    /// <summary>
    /// Creates margings from the specified left, top, right, and bottom thickness.
    /// </summary>
    public Margins(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>
    /// Creates margings of uniform dimensions on all sides.
    /// </summary>
    public Margins(int thickness) : this(thickness, thickness, thickness, thickness) {}

    /// <summary>
    /// Creates margings of the given horizontal and vertical dimensions, each applied to both relevant sides.
    /// </summary>
    /// <param name="horizontal">The margin thickness of the <see cref="Left"/> and <see cref="Right"/> sides.</param>
    /// <param name="vertical">The margin thickness of the <see cref="Top"/> and <see cref="Bottom"/> sides.</param>
    /// <remarks>
    /// A <see cref="Margins"/> instance created with a <paramref name="horizontal"/> thickness of 2 and a
    /// <paramref name="vertical"/> thickness of 1 will have the margins <c>(2, 1, 2, 1)</c>.
    /// </remarks>
    public Margins(int horizontal, int vertical) : this(horizontal, vertical, horizontal, vertical) {}

    public static readonly Margins Zero = new(0, 0, 0, 0);

    /// <summary>
    /// Gets the total horizontal margin by summing the values of the left and right margins.
    /// </summary>
    /// <remarks>
    /// The horizontal margin represents the combined width of the left and right padding areas
    /// within a <see cref="Margins"/> instance. This value is computed as the sum of the
    /// <c>Left</c> and <c>Right</c> properties.
    /// </remarks>
    public int Horizontal => Left + Right;

    /// <summary>
    /// Gets the total vertical margin, which is the sum of the top and bottom margins.
    /// </summary>
    public int Vertical => Top + Bottom;

    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }

    /// <summary>
    /// Returns a string representation of the margins object, including the values of
    /// the left, top, right, and bottom margins.
    /// </summary>
    /// <returns>
    /// A string formatted as "Margins(L:{Left}, T:{Top}, R:{Right}, B:{Bottom})",
    /// where {Left}, {Top}, {Right}, and {Bottom} are the respective values of the margins.
    /// </returns>
    public override string ToString() => $"Margins(L:{Left}, T:{Top}, R:{Right}, B:{Bottom})";

    public void Deconstruct(out int Left, out int Top, out int Right, out int Bottom)
    {
        Left = this.Left;
        Top = this.Top;
        Right = this.Right;
        Bottom = this.Bottom;
    }
}