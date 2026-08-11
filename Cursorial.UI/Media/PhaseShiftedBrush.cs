using Cursorial.Markup;
using Cursorial.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;

namespace Cursorial.UI.Media;

/// <summary>
/// An animation-friendly brush decorator: wraps an inner <see cref="IBrush"/> and advances its
/// parameter axis by an animatable <see cref="Phase"/> before sampling — the marquee/shimmer
/// brush. Extending <see cref="UIObject"/> (this is the property-system tier of the media stack,
/// one layer above the immutable <c>Cursorial.Rendering.Media</c> brushes) buys the full property
/// surface for <see cref="PhaseProperty"/>: animation (<c>BeginAnimation</c> drives it per frame,
/// mutating this instance in place — no brush is allocated per tick, and every cache keyed on the
/// brush <em>reference</em> stays hot), binding, and styling. Repaint comes from the property
/// system's sub-object observation: an element holding this brush in a brush slot auto-subscribes,
/// and a <see cref="Phase"/> tick routes <see cref="PropertyEffects.AffectsRender"/> through that
/// slot's effect ceiling — the element re-rasters without any value-changed callback firing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase semantics.</b> <see cref="Phase"/> is a normalized offset in gradient-parameter
/// periods, added to the inner brush's <em>raw</em> parameter <c>t</c> before its out-of-range
/// policy applies (<see cref="IBrush.ColorAt(int, int, Rect, double)"/>): the inner gradient's own
/// <see cref="GradientSpread"/> decides what a shifted parameter means — <see cref="GradientSpread.Repeat"/>
/// wraps (animating 0 → 1 slides the pattern one full period; the marquee), <see cref="GradientSpread.Reflect"/>
/// ping-pongs, and <see cref="GradientSpread.Pad"/> clamps (the pattern slides off and saturates).
/// Increasing the phase advances the sampled parameter, so the visible pattern slides
/// <em>toward</em> the gradient's start point; animate downward (or swap the endpoints) for the
/// other direction. Brushes without a parameter axis ignore the phase entirely: a wrapped solid
/// samples identically at every phase (and stays <see cref="IsUniform"/>), and image/tile brushes
/// currently sample unshifted. Wrapping another <see cref="PhaseShiftedBrush"/> composes the
/// phases additively.
/// </para>
/// <para>
/// <b>Deviation from the <see cref="IBrush"/> immutability note.</b> This brush is deliberately
/// mutable — that is its entire point — and therefore UI-thread-affine like every
/// <see cref="UIObject"/>: sample it only on the thread that owns it (the render pass runs there
/// by contract, invariant 6). <see cref="ColorAt(int, int, Rect)"/> remains pure and
/// allocation-free per sample.
/// </para>
/// </remarks>
[ContentProperty(nameof(Brush))]
public sealed class PhaseShiftedBrush : UIObject, IBrush
{
    /// <summary>The wrapped brush the phase-shifted sampling delegates to. With no inner brush the
    /// wrapper samples the terminal default color (the stop-less-gradient convention).</summary>
    public static readonly StyledProperty<IBrush?> BrushProperty =
        UIProperty.Register<PhaseShiftedBrush, IBrush?>(nameof(Brush));

    /// <summary>The phase offset, in gradient-parameter periods (any finite value; 1.0 is one full
    /// period). Added to the inner brush's raw parameter before its <see cref="GradientSpread"/>
    /// applies — see the class remarks for the wrap/clamp semantics. The animatable knob.</summary>
    public static readonly StyledProperty<double> PhaseProperty =
        UIProperty.Register<PhaseShiftedBrush, double>(nameof(Phase), validate: double.IsFinite);

    static PhaseShiftedBrush()
    {
        // The declared effect levels the sub-object observation clamps against: both knobs change
        // painted ink only — render-level, never measure.
        AffectsRender<PhaseShiftedBrush>(BrushProperty, PhaseProperty);
    }

    /// <summary>Creates an empty wrapper (XAML element authoring: the inner brush is the content property).</summary>
    public PhaseShiftedBrush() { }

    /// <summary>Creates a wrapper over <paramref name="brush"/>, optionally pre-shifted by <paramref name="phase"/>.</summary>
    public PhaseShiftedBrush(IBrush? brush, double phase = 0.0)
    {
        Brush = brush;
        if (phase != 0.0)
            Phase = phase;
    }

    /// <inheritdoc cref="BrushProperty"/>
    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    /// <inheritdoc cref="PhaseProperty"/>
    public double Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    /// <summary>Delegates to the inner brush: a phase-shifted gradient stays non-uniform, a wrapped
    /// solid stays uniform (the phase is vacuous there), and an empty wrapper is uniform.</summary>
    public bool IsUniform => Brush?.IsUniform ?? true;

    /// <summary>Delegates to the inner brush (the wrapper adds no opacity of its own).</summary>
    public double Opacity => Brush?.Opacity ?? 1.0;

    /// <summary>Delegates to the inner brush; an empty wrapper samples the terminal default, which carries no alpha.</summary>
    public bool IsOpaque => Brush?.IsOpaque ?? true;

    /// <inheritdoc/>
    public Color ColorAt(int column, int row, Rect bounds)
        => Brush?.ColorAt(column, row, bounds, Phase) ?? Colors.Default;

    /// <summary>The phase-composing sample (<see cref="IBrush.ColorAt(int, int, Rect, double)"/>):
    /// an outer phase adds to this wrapper's own, so nested wrappers shift additively.</summary>
    public Color ColorAt(int column, int row, Rect bounds, double phase)
        => Brush?.ColorAt(column, row, bounds, Phase + phase) ?? Colors.Default;
}
