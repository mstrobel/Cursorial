// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>A 2-D data point in a chart's value space (<see cref="X"/>, <see cref="Y"/>).</summary>
public readonly record struct PointD(double X, double Y)
{
    /// <summary>Convenience conversion from an <c>(x, y)</c> tuple.</summary>
    public static implicit operator PointD((double X, double Y) p) => new(p.X, p.Y);
}
