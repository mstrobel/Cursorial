using System.Globalization;

// ReSharper disable CheckNamespace
namespace Cursorial.UI;

/// <summary>
/// How many times an animation track repeats (design doc §9.3): <see cref="Once"/>, a finite
/// <see cref="Count"/> (≥ 1), or <see cref="Forever"/>. The XAML grammar is <c>"1x"</c>/<c>"3x"</c>/
/// <c>"Forever"</c> (WPF parity). Mirrors <see cref="Cursorial.Animation.RepeatAnimation{T}"/>'s
/// count axis — a track wraps its built animation in a <c>RepeatAnimation</c> per this value.
/// </summary>
public readonly record struct RepeatBehavior
{
    // Encoding: _count >= 1 ⇒ finite; _count == 0 ⇒ Forever. default(RepeatBehavior) == Once (Count 1
    // would be the natural default, but a zero-init struct must be a sane "play once" — so 0 maps to
    // Forever only through the named factory, and the parameterless default is normalized to Once).
    private readonly int _iterations; // 0 when Forever; >= 1 finite; the default(struct) sentinel is handled below
    private readonly bool _forever;

    private RepeatBehavior(int iterations, bool forever)
    {
        _iterations = iterations;
        _forever = forever;
    }

    /// <summary>Play exactly once (the default).</summary>
    public static RepeatBehavior Once => new(1, forever: false);

    /// <summary>Repeat forever (perpetual).</summary>
    public static RepeatBehavior Forever => new(0, forever: true);

    /// <summary>Repeat a finite number of times (<paramref name="count"/> ≥ 1).</summary>
    public static RepeatBehavior Count(int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Repeat count must be at least 1.");
        return new RepeatBehavior(count, forever: false);
    }

    /// <summary>True when this repeats forever.</summary>
    public bool IsForever => _forever;

    /// <summary>The finite iteration count, or <see langword="null"/> when <see cref="IsForever"/>.</summary>
    /// <remarks><c>default(RepeatBehavior)</c> normalizes to a single iteration (play once).</remarks>
    public int? IterationCount => _forever ? null : (_iterations < 1 ? 1 : _iterations);

    /// <summary>Parses <c>"Forever"</c>, <c>"Nx"</c>, or a bare integer <c>"N"</c> (Fork C converter; §9.10).</summary>
    public static bool TryParse(string? text, out RepeatBehavior value)
    {
        value = Once;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().Trim();
        if (span.Equals("Forever", StringComparison.OrdinalIgnoreCase))
        {
            value = Forever;
            return true;
        }

        if (span.Length > 0 && (span[^1] is 'x' or 'X'))
            span = span[..^1];

        if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 1)
        {
            value = Count(count);
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => _forever ? "Forever" : $"{IterationCount}x";
}
