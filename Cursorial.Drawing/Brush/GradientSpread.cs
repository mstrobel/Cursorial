// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>How a gradient behaves outside its [0, 1] parameter range.</summary>
public enum GradientSpread : byte
{
    /// <summary>Clamp to the first/last stop color (the default).</summary>
    Pad = 0,

    /// <summary>Tile the gradient (wrap the parameter into [0, 1)).</summary>
    Repeat = 1,

    /// <summary>Mirror the gradient on each repeat.</summary>
    Reflect = 2
}
