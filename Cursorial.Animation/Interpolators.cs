namespace Cursorial.Animation;

/// <summary>
/// Discoverability shortcuts for the built-in interpolators. (The <c>Color</c> and gradient/brush
/// interpolators live in <c>Cursorial.Drawing</c>, since their value types do.)
/// </summary>
public static class Interpolators
{
    /// <summary>Linear <see cref="double"/> interpolation.</summary>
    public static IInterpolator<double> Double => DoubleInterpolator.Instance;

    /// <summary>Rounded linear <see cref="int"/> interpolation.</summary>
    public static IInterpolator<int> Int32 => Int32Interpolator.Instance;
}
