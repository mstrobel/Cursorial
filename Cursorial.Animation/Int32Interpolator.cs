namespace Cursorial.Animation;

/// <summary>
/// Linear interpolation between two <see cref="int"/> values, rounded to the nearest integer
/// (ties away from zero). Suited to integer targets like cell offsets. Stateless singleton.
/// </summary>
public sealed class Int32Interpolator : IInterpolator<int>
{
    /// <summary>The shared instance.</summary>
    public static Int32Interpolator Instance { get; } = new();

    private Int32Interpolator() { }

    /// <inheritdoc/>
    public int Interpolate(int from, int to, double progress) =>
        (int) Math.Round(from + (to - from) * progress, MidpointRounding.AwayFromZero);
}
