using Cursorial.Media;

namespace Cursorial.Animation;

/// <summary>
/// Discoverability shortcuts for the built-in interpolators. (The gradient/brush and
/// <c>RelativePoint</c> interpolators live in <c>Cursorial.Drawing</c>, since those value types do.)
/// </summary>
public static class Interpolators
{
    /// <summary>Linear <see cref="double"/> interpolation.</summary>
    public static IInterpolator<double> Double => DoubleInterpolator.Instance;

    /// <summary>Rounded linear <see cref="int"/> interpolation.</summary>
    public static IInterpolator<int> Int32 => Int32Interpolator.Instance;

    /// <summary>Premultiplied-sRGB <see cref="Media.Color"/> interpolation.</summary>
    public static IInterpolator<Color> Color => ColorInterpolator.Instance;
}
