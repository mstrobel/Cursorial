namespace Cursorial.Rendering.Media;

/// <summary>A color placed at a normalized offset (0–1) along a gradient.</summary>
public readonly record struct GradientStop
{
    private readonly double _offset;

    /// <summary>Create a stop. <paramref name="offset"/> is clamped to [0, 1].</summary>
    public GradientStop(double offset, Cursorial.Media.Color color)
    {
        Offset = offset;
        Color = color;
    }

    /// <summary>Position along the gradient, 0–1 (clamped); settable via init for XAML element authoring.</summary>
    public double Offset { get => _offset; init => _offset = Math.Clamp(value, 0.0, 1.0); }

    /// <summary>The color at this offset; settable via init for XAML element authoring.</summary>
    public Cursorial.Media.Color Color { get; init; }

    /// <summary>
    /// Convenience conversion from an <c>(offset, color)</c> tuple, so a stop list can be written as
    /// <c>[(0.0, Colors.Red), (1.0, Colors.Blue)]</c> without spelling <c>new GradientStop(...)</c>.
    /// </summary>
    public static implicit operator GradientStop((double Offset, Cursorial.Media.Color Color) stop) => new(stop.Offset, stop.Color);
}
