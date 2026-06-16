using Cursorial.Drawing.Media;
using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A horizontal progress indicator (design doc §12.7). Determinate: fills <c>(Value−Minimum)/(Maximum−Minimum)</c>
/// of the track with <see cref="Fill"/> (the rest is the track <see cref="Control.Background"/>). Indeterminate
/// (<see cref="IsIndeterminate"/> ⇒ <c>:indeterminate</c>): a sweep block drawn at <see cref="IndeterminateOffset"/>.
/// The bar is painted directly in <see cref="Render"/> as solid cells (the cell grid's block-bar idiom).
/// </summary>
/// <remarks>The animated indeterminate sweep — a storyboard-ignited, composite-only persistent highlight layer
/// (design doc §3.9 resolution 1, <see cref="IndeterminateOffset"/> as an <c>AffectsComposite</c> target) — is a
/// recorded deferral; v1 draws the offset block in <see cref="Render"/> (a consumer can animate the offset).</remarks>
public class ProgressBar : Control
{
    /// <summary>The lower bound of <see cref="Value"/> (default 0; AffectsRender).</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        UIProperty.Register<ProgressBar, double>(nameof(Minimum), defaultValue: 0);

    /// <summary>The upper bound of <see cref="Value"/> (default 100; AffectsRender).</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        UIProperty.Register<ProgressBar, double>(nameof(Maximum), defaultValue: 100);

    /// <summary>The current value, coerced into [<see cref="Minimum"/>, <see cref="Maximum"/>] (default 0; AffectsRender).</summary>
    public static readonly StyledProperty<double> ValueProperty =
        UIProperty.Register<ProgressBar, double>(nameof(Value), defaultValue: 0, coerce: CoerceValue);

    /// <summary>Whether the bar shows the indeterminate sweep instead of a determinate fill (<c>:indeterminate</c>).</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        UIProperty.Register<ProgressBar, bool>(nameof(IsIndeterminate));

    /// <summary>The fill (bar) brush; the track is <see cref="Control.Background"/>. AffectsRender.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        UIProperty.Register<ProgressBar, IBrush?>(nameof(Fill));

    /// <summary>The indeterminate sweep block's left column (the animation target; v1 AffectsRender — the
    /// composite-only persistent layer is deferred, §3.9 resolution 1).</summary>
    public static readonly StyledProperty<int> IndeterminateOffsetProperty =
        UIProperty.Register<ProgressBar, int>(nameof(IndeterminateOffset));

    static ProgressBar()
    {
        PseudoClassMapping.Register<ProgressBar>(IsIndeterminateProperty, ":indeterminate");
        AffectsRender<ProgressBar>(MinimumProperty, MaximumProperty, ValueProperty, IsIndeterminateProperty,
            FillProperty, IndeterminateOffsetProperty);
    }

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="MaximumProperty"/>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="IsIndeterminateProperty"/>
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    /// <inheritdoc cref="FillProperty"/>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <inheritdoc cref="IndeterminateOffsetProperty"/>
    public int IndeterminateOffset { get => GetValue(IndeterminateOffsetProperty); set => SetValue(IndeterminateOffsetProperty, value); }

    /// <summary>The filled fraction in [0, 1] (0 when the range is empty). Determinate only.</summary>
    public double FilledFraction
    {
        get
        {
            var range = Maximum - Minimum;
            return range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        }
    }

    /// <summary>The bar stretches horizontally and is one row tall by default.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(0, 1);

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var size = context.Size;
        if (size.Columns <= 0 || size.Rows <= 0)
            return;

        var width = size.Columns;
        var height = size.Rows;

        if (Background is { } track)
            context.FillRectangle(new Rect(0, 0, width, height), track); // the recessed track behind the bar

        if (Fill is not { } fill)
            return;

        if (IsIndeterminate)
        {
            // A sweep block (~1/3 of the track), positioned at the offset and clamped inside the bounds. A consumer
            // (or, later, the deferred storyboard) sweeps IndeterminateOffset to animate it.
            var blockWidth = Math.Max(1, width / 3);
            var x = Math.Clamp(IndeterminateOffset, 0, Math.Max(0, width - blockWidth));
            context.FillRectangle(new Rect(x, 0, blockWidth, height), fill);
        }
        else
        {
            var filled = (int)Math.Round(FilledFraction * width);
            if (filled > 0)
                context.FillRectangle(new Rect(0, 0, filled, height), fill);
        }
    }

    // Clamp the value into [Minimum, Maximum]; a misconfigured range (Max < Min) is left as-is (Render clamps the ratio).
    private static double CoerceValue(UIObject owner, double value)
    {
        var bar = (ProgressBar)owner;
        var lo = bar.Minimum;
        var hi = bar.Maximum;
        return hi < lo ? value : Math.Clamp(value, lo, hi);
    }
}
