using Cursorial.Animation;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// A horizontal progress indicator (design doc §12.7). Determinate: fills <c>(Value−Minimum)/(Maximum−Minimum)</c>
/// of the track with <see cref="Fill"/> (the rest is the track <see cref="Control.Background"/>). Indeterminate
/// (<see cref="IsIndeterminate"/> ⇒ <c>:indeterminate</c>): a ~⅓-width sweep block that marquees back and forth,
/// driven by a perpetual S5 animation on the normalized <see cref="IndeterminatePhase"/> (which
/// <see cref="Render"/> maps to a column). The bar is painted directly in <see cref="Render"/> as solid cells.
/// </summary>
/// <remarks>The marquee is a control-owned <see cref="ElementAnimationExtensions.BeginAnimation{T}"/> run on
/// <see cref="IndeterminatePhaseProperty"/> (started on attach / when <see cref="IsIndeterminate"/> turns on,
/// stopped when it turns off; the scheduler detach-stops it, §9.6; reduced motion (<c>AnimationsEnabled=false</c>)
/// holds the phase, AD15). The phase is width-independent, so no re-arm on resize. (The composite-only persistent
/// highlight layer of §3.9 resolution 1 — an <c>AffectsComposite</c> target instead of the per-frame
/// <c>AffectsRender</c> re-raster of this small bar — stays a recorded perf refinement.)</remarks>
public class ProgressBar : RangeBase
{
    // The marquee bounces the phase [0,1] over this period each way (a perpetual AutoReverse).
    private static readonly TimeSpan MarqueePeriod = TimeSpan.FromMilliseconds(1100);

    /// <summary>Whether the bar shows the indeterminate marquee instead of a determinate fill (<c>:indeterminate</c>).</summary>
    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        UIProperty.Register<ProgressBar, bool>(nameof(IsIndeterminate), changed: OnIsIndeterminateChanged);

    /// <summary>The fill (bar) brush; the track is <see cref="Control.Background"/>. AffectsRender.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        UIProperty.Register<ProgressBar, IBrush?>(nameof(Fill));

    /// <summary>The indeterminate marquee's normalized phase in [0, 1] (0 = block fully left, 1 = fully right) —
    /// the animation target; <see cref="Render"/> maps it to the sweep block's column, so it is width-independent.
    /// AffectsRender.</summary>
    public static readonly StyledProperty<double> IndeterminatePhaseProperty =
        UIProperty.Register<ProgressBar, double>(nameof(IndeterminatePhase));

    static ProgressBar()
    {
        // Min/Max/Value (+ clamp coercion) come from RangeBase; only the default Maximum differs (100, not 1).
        MaximumProperty.OverrideDefaultValue<ProgressBar>(100);

        PseudoClassMapping.Register<ProgressBar>(IsIndeterminateProperty, ":indeterminate");
        AffectsRender<ProgressBar>(IsIndeterminateProperty, FillProperty, IndeterminatePhaseProperty);
    }

    /// <inheritdoc cref="IsIndeterminateProperty"/>
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }

    /// <inheritdoc cref="FillProperty"/>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <inheritdoc cref="IndeterminatePhaseProperty"/>
    public double IndeterminatePhase { get => GetValue(IndeterminatePhaseProperty); set => SetValue(IndeterminatePhaseProperty, value); }

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

        if (Background is {} track)
            context.FillOpaque(new Rect(0, 0, width, height), track, overwrite: true); // the recessed track behind the bar

        if (Fill is not {} fill)
            return;

        var attributes = TextElement.ComposeAttributes(this).Flags & (TextAttributes.Inverse | TextAttributes.Blink);
        
        if (IsIndeterminate)
        {
            // A sweep block (~1/3 of the track) positioned from the marquee phase: 0 → fully left, 1 → fully right.
            var blockWidth = Math.Max(1, width / 3);
            var travel = Math.Max(0, width - blockWidth);
            var x = (int)Math.Round(Math.Clamp(IndeterminatePhase, 0, 1) * travel);
            context.FillOpaque(new Rect(x, 0, blockWidth, height), fill, 
                               attributes, overwrite: true);
        }
        else
        {
            var filled = (int)Math.Round(FilledFraction * width);
            if (filled > 0)
                context.FillOpaque(new Rect(0, 0, filled, height), fill, 
                                   attributes, overwrite: true);
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        StartMarquee(); // (re)start after attach — a prior detach stopped it (§9.6); no-op when not indeterminate
    }

    // IsIndeterminate flipped: start/stop the marquee animation. (Detach is handled by the scheduler, §9.6.)
    private static void OnIsIndeterminateChanged(UIObject sender, bool oldValue, bool newValue)
    {
        var bar = (ProgressBar)sender;
        if (newValue)
            bar.StartMarquee();
        else
            bar.StopMarquee();
    }

    // A perpetual bounce of the normalized phase [0, 1]; Render maps it to the sweep block's column. Only when
    // attached to a live tree with an ambient scheduler — the S5 layer honors AnimationsEnabled (reduced motion
    // holds the phase, AD15) and detach-stops it (§9.6). BeginAnimation replaces any prior run (SnapshotAndReplace).
    private void StartMarquee()
    {
        if (!IsAttachedToTree || !IsIndeterminate || AnimationScheduler.CurrentOrNull is null)
            return;

        this.BeginAnimation(IndeterminatePhaseProperty, new DoubleAnimation(0.0, 1.0, MarqueePeriod).AutoReverse());
    }

    private void StopMarquee() => this.StopAnimation(IndeterminatePhaseProperty);
}
