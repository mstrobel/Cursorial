using static System.Math;

namespace Cursorial.Animation;

/// <summary>
/// The built-in <see cref="Easing"/> catalog (the standard Penner / easings.net curves). Each entry
/// is a cached, allocation-free delegate. <c>In</c> accelerates from rest, <c>Out</c> decelerates to
/// rest, <c>InOut</c> does both; <see cref="BackIn"/>/<see cref="BackOut"/>/<see cref="BackInOut"/>
/// deliberately overshoot for an anticipation / settle feel.
/// </summary>
public static class Easings
{
    // Back-easing overshoot constants (easings.net): c1 governs the In/Out overshoot, c2 the InOut.
    private const double C1 = 1.70158;
    private const double C2 = C1 * 1.525;
    private const double C3 = C1 + 1.0;

    /// <summary>No shaping — progress passes through unchanged.</summary>
    public static Easing Linear { get; } = t => t;

    /// <summary>Quadratic (<c>t²</c>) ease-in.</summary>
    public static Easing QuadIn { get; } = t => t * t;
    /// <summary>Quadratic ease-out.</summary>
    public static Easing QuadOut { get; } = t => 1.0 - (1.0 - t) * (1.0 - t);
    /// <summary>Quadratic ease-in-out.</summary>
    public static Easing QuadInOut { get; } = t => t < 0.5 ? 2.0 * t * t : 1.0 - Pow(-2.0 * t + 2.0, 2.0) / 2.0;

    /// <summary>Cubic (<c>t³</c>) ease-in.</summary>
    public static Easing CubicIn { get; } = t => t * t * t;
    /// <summary>Cubic ease-out.</summary>
    public static Easing CubicOut { get; } = t => 1.0 - Pow(1.0 - t, 3.0);
    /// <summary>Cubic ease-in-out.</summary>
    public static Easing CubicInOut { get; } = t => t < 0.5 ? 4.0 * t * t * t : 1.0 - Pow(-2.0 * t + 2.0, 3.0) / 2.0;

    /// <summary>Quartic (<c>t⁴</c>) ease-in.</summary>
    public static Easing QuartIn { get; } = t => t * t * t * t;
    /// <summary>Quartic ease-out.</summary>
    public static Easing QuartOut { get; } = t => 1.0 - Pow(1.0 - t, 4.0);
    /// <summary>Quartic ease-in-out.</summary>
    public static Easing QuartInOut { get; } = t => t < 0.5 ? 8.0 * t * t * t * t : 1.0 - Pow(-2.0 * t + 2.0, 4.0) / 2.0;

    /// <summary>Sinusoidal ease-in.</summary>
    public static Easing SineIn { get; } = t => 1.0 - Cos(t * PI / 2.0);
    /// <summary>Sinusoidal ease-out.</summary>
    public static Easing SineOut { get; } = t => Sin(t * PI / 2.0);
    /// <summary>Sinusoidal ease-in-out.</summary>
    public static Easing SineInOut { get; } = t => -(Cos(PI * t) - 1.0) / 2.0;

    /// <summary>Exponential (<c>2^(10(t-1))</c>) ease-in.</summary>
    public static Easing ExpoIn { get; } = t => t <= 0.0 ? 0.0 : Pow(2.0, 10.0 * t - 10.0);
    /// <summary>Exponential ease-out.</summary>
    public static Easing ExpoOut { get; } = t => t >= 1.0 ? 1.0 : 1.0 - Pow(2.0, -10.0 * t);
    /// <summary>Exponential ease-in-out.</summary>
    public static Easing ExpoInOut { get; } = t =>
        t <= 0.0 ? 0.0
        : t >= 1.0 ? 1.0
        : t < 0.5 ? Pow(2.0, 20.0 * t - 10.0) / 2.0
        : (2.0 - Pow(2.0, -20.0 * t + 10.0)) / 2.0;

    /// <summary>Anticipation ease-in: dips below 0 before accelerating.</summary>
    public static Easing BackIn { get; } = t => C3 * t * t * t - C1 * t * t;
    /// <summary>Settle ease-out: overshoots past 1 before resting.</summary>
    public static Easing BackOut { get; } = t => 1.0 + C3 * Pow(t - 1.0, 3.0) + C1 * Pow(t - 1.0, 2.0);
    /// <summary>Anticipation-and-settle ease-in-out: undershoots then overshoots.</summary>
    public static Easing BackInOut { get; } = t =>
        t < 0.5
            ? Pow(2.0 * t, 2.0) * ((C2 + 1.0) * 2.0 * t - C2) / 2.0
            : (Pow(2.0 * t - 2.0, 2.0) * ((C2 + 1.0) * (2.0 * t - 2.0) + C2) + 2.0) / 2.0;
}
