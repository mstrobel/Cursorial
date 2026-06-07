namespace Cursorial.Drawing;

/// <summary>The geometry of a gradient — how a cell position maps to a position along the stops.</summary>
public enum GradientKind : byte
{
    /// <summary>Color varies along the line from the start point to the end point.</summary>
    Linear = 0,

    /// <summary>Color varies with (elliptical) distance from a center, optionally toward a focal point.</summary>
    Radial = 1,

    /// <summary>Color varies with the angle swept around a center (a "sweep" gradient).</summary>
    Conic = 2
}
