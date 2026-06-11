using Cursorial.Animation;

// ReSharper disable CheckNamespace

namespace Cursorial.Drawing;

/// <summary>
/// Interpolates between two <see cref="Pen"/>s — the per-frame target for an animated stroke
/// (e.g., a border sweeping through a gradient, or a focus ring pulsing its color).
/// </summary>
/// <remarks>
/// Only the color source blends: both endpoints carrying a brush route through
/// <see cref="BrushInterpolator"/> (same-shape brushes blend their parameters; disparate shapes snap).
/// A <see langword="null"/> brush means "terminal default foreground" — there is nothing to blend
/// against, so when either endpoint is null the brush snaps at the midpoint with the rest of the
/// discrete state. Every other member (<see cref="Pen.Weight"/>, <see cref="Pen.Corners"/>,
/// <see cref="Pen.Dash"/>, <see cref="Pen.EndCap"/>, <see cref="Pen.Junction"/>,
/// <see cref="Pen.GlyphSet"/>, <see cref="Pen.Attributes"/>) selects a glyph family or flag rather
/// than a continuous quantity, so they snap at the midpoint (<c>from</c>'s when
/// <c>progress &lt; 0.5</c>, else <c>to</c>'s) — typically identical across both endpoints anyway.
/// Stateless singleton.
/// </remarks>
public sealed class PenInterpolator : IInterpolator<Pen>
{
    /// <summary>The shared instance.</summary>
    public static PenInterpolator Instance { get; } = new();

    private PenInterpolator() { }

    /// <inheritdoc/>
    public Pen Interpolate(Pen from, Pen to, double progress)
    {
        var discrete = progress < 0.5 ? from : to;   // weight/corners/dash/cap/junction/glyphs/attributes snap

        var brush = (from.Brush, to.Brush) switch
                    {
                        (null, null) => null,
                        ({} a, {} b) => ReferenceEquals(a, b) ? a : BrushInterpolator.Instance.Interpolate(a, b, progress),
                        _            => discrete.Brush // one side is the terminal default — nothing to blend; snap
                    };

        return discrete with { Brush = brush };
    }
}
