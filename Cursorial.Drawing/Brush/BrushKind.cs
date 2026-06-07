namespace Cursorial.Drawing;

/// <summary>
/// Discriminates a <see cref="Brush"/>'s color source. Mirrors the closed-kind shape of
/// <see cref="Cursorial.Output.Color"/>. Phase 1 implements only <see cref="Solid"/>; the gradient
/// kinds are reserved and constructed via factories that arrive with gradient sampling.
/// </summary>
public enum BrushKind : byte
{
    /// <summary>A single solid color (optionally with reduced opacity folded into its alpha).</summary>
    Solid = 0,

    /// <summary>A linear gradient (Phase 2).</summary>
    Linear = 1,

    /// <summary>A radial gradient (Phase 2).</summary>
    Radial = 2,

    /// <summary>A conic gradient (Phase 2).</summary>
    Conic = 3
}
