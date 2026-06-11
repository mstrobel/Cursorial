// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>How a <see cref="LineChart"/> connects its data points.</summary>
public enum CurveInterpolation
{
    /// <summary>Straight segments between points. The default.</summary>
    Linear,

    /// <summary>A smooth centripetal Catmull-Rom spline (α = 0.5) — cusp-free, for paths that may double
    /// back. May overshoot the data (it interpolates, it doesn't preserve shape).</summary>
    CatmullRom,

    /// <summary>A Fritsch-Carlson monotone cubic — shape-preserving, <b>no overshoot</b>. Its precondition
    /// is strictly-increasing X (a function graph); points are sorted by X if they aren't already.</summary>
    MonotoneCubic
}
