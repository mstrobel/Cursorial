namespace Cursorial.Animation;

/// <summary>Linear interpolation between two <see cref="double"/> values. Stateless singleton.</summary>
public sealed class DoubleInterpolator : IInterpolator<double>
{
    /// <summary>The shared instance.</summary>
    public static DoubleInterpolator Instance { get; } = new();

    private DoubleInterpolator() { }

    /// <inheritdoc/>
    public double Interpolate(double from, double to, double progress) => from + (to - from) * progress;
}
