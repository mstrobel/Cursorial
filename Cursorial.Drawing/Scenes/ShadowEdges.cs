namespace Cursorial.Drawing;

/// <summary>The element edges a shadow is cast from.</summary>
[Flags]
public enum ShadowEdges : byte
{
    /// <summary>No edges cast (a no-op shadow).</summary>
    None = 0,

    /// <summary>The left edge.</summary>
    Left = 1,

    /// <summary>The top edge.</summary>
    Top = 2,

    /// <summary>The right edge.</summary>
    Right = 4,

    /// <summary>The bottom edge.</summary>
    Bottom = 8,

    /// <summary>All four edges.</summary>
    All = Left | Top | Right | Bottom,
}
