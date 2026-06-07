using Cursorial.Output;

namespace Cursorial.Drawing;

/// <summary>A color placed at a normalized offset (0–1) along a gradient.</summary>
public readonly record struct GradientStop
{
    /// <summary>Create a stop. <paramref name="offset"/> is clamped to [0, 1].</summary>
    public GradientStop(double offset, Color color)
    {
        Offset = Math.Clamp(offset, 0.0, 1.0);
        Color = color;
    }

    /// <summary>Position along the gradient, 0–1.</summary>
    public double Offset { get; }

    /// <summary>The color at this offset.</summary>
    public Color Color { get; }
}
