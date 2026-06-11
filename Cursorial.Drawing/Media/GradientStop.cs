using Cursorial.Output;

namespace Cursorial.Drawing.Media;

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

    /// <summary>
    /// Convenience conversion from an <c>(offset, color)</c> tuple, so a stop list can be written as
    /// <c>[(0.0, Colors.Red), (1.0, Colors.Blue)]</c> without spelling <c>new GradientStop(...)</c>.
    /// </summary>
    public static implicit operator GradientStop((double Offset, Color Color) stop) => new(stop.Offset, stop.Color);
}
