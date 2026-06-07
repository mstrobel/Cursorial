namespace Cursorial.Drawing;

/// <summary>
/// Discriminates a <see cref="Brush"/>'s color source. Mirrors the closed-kind shape of
/// <see cref="Cursorial.Output.Color"/>.
/// </summary>
public enum BrushKind : byte
{
    /// <summary>A single solid color (optionally with reduced opacity folded into its alpha).</summary>
    Solid = 0,

    /// <summary>A linear gradient.</summary>
    Linear = 1,

    /// <summary>A radial gradient.</summary>
    Radial = 2,

    /// <summary>A conic gradient.</summary>
    Conic = 3
}
