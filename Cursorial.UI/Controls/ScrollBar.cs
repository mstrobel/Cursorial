using Cursorial.Drawing.Media;
using Cursorial.Output;

using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A scroll bar (design doc §12.7, CD28): a 1-cell-wide (or 1-cell-tall) <see cref="Control"/> with a
/// <see cref="Value"/> ∈ <c>[Minimum, Maximum]</c> model, a proportional draggable thumb on a
/// <see cref="Track"/> rail, click-track paging (±<see cref="LargeChange"/>), and optional
/// line-step <see cref="RepeatButton"/>s (<c>PART_LineUpButton</c>/<c>PART_LineDownButton</c>) that
/// repeat ±<see cref="SmallChange"/> while held. <see cref="Orientation"/> (S1-owned) selects the
/// axis; the <c>:horizontal</c>/<c>:vertical</c> pseudo-classes select glyph/orientation styling. A
/// value change raises the bubbling <see cref="Scroll"/> event (<see cref="ScrollEventArgs"/>); the
/// owning <see cref="ScrollViewer"/> wires it code-behind in <c>OnApplyTemplate</c>.
/// </summary>
/// <remarks>
/// The required part is <c>PART_Track</c> (a <see cref="Controls.Track"/>); the arrow line-buttons are
/// optional and the bar degrades to track-only scrolling when a template omits them (CD19/C236).
/// </remarks>
[TemplatePart(PartTrack, typeof(Track), IsRequired = true)]
[TemplatePart(PartLineUp, typeof(RepeatButton))]
[TemplatePart(PartLineDown, typeof(RepeatButton))]
public class ScrollBar : Control
{
    private const string PartTrack = "PART_Track";
    private const string PartLineUp = "PART_LineUpButton";
    private const string PartLineDown = "PART_LineDownButton";

    private RepeatButton? _lineUp;
    private RepeatButton? _lineDown;
    private bool _suppressScrollEvent;     // set during a Value set whose owner re-raises with its own type

    /// <summary>The bar's axis (S1-owned <see cref="Controls.Orientation"/>; default <see cref="Orientation.Vertical"/>). <c>AffectsMeasure</c> + the <c>:horizontal</c>/<c>:vertical</c> classes.</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<ScrollBar, Orientation>(nameof(Orientation), defaultValue: Orientation.Vertical, changed: OnOrientationChanged);

    /// <summary>The current scroll value, coerced into <c>[Minimum, Maximum]</c> (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<double> ValueProperty =
        UIProperty.Register<ScrollBar, double>(nameof(Value), coerce: CoerceValue, changed: OnValueChanged);

    /// <summary>The minimum value (default 0). <c>AffectsRender</c>.</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        UIProperty.Register<ScrollBar, double>(nameof(Minimum), changed: OnRangeChanged);

    /// <summary>The maximum value (default 0 ⇒ no travel until set). <c>AffectsRender</c>.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        UIProperty.Register<ScrollBar, double>(nameof(Maximum), changed: OnRangeChanged);

    /// <summary>The viewport size in value units (the proportional thumb length input). <c>AffectsRender</c>.</summary>
    public static readonly StyledProperty<double> ViewportSizeProperty =
        UIProperty.Register<ScrollBar, double>(nameof(ViewportSize), changed: OnRangeChanged);

    /// <summary>The line-step delta (arrow buttons; default 1).</summary>
    public static readonly StyledProperty<double> SmallChangeProperty =
        UIProperty.Register<ScrollBar, double>(nameof(SmallChange), defaultValue: 1.0);

    /// <summary>The page-step delta (track click; default 0 ⇒ falls back to <see cref="ViewportSize"/>).</summary>
    public static readonly StyledProperty<double> LargeChangeProperty =
        UIProperty.Register<ScrollBar, double>(nameof(LargeChange));

    /// <summary>The thumb fill brush (the proportional <c>█</c> run; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        UIProperty.Register<ScrollBar, IBrush?>(nameof(ThumbBrush));

    /// <summary>The bubbling scroll event raised whenever <see cref="Value"/> changes (<see cref="ScrollEventArgs"/>).</summary>
    public static readonly RoutedEvent<ScrollEventArgs> ScrollEvent =
        RoutedEvent<ScrollEventArgs>.Register(nameof(Scroll), RoutingStrategy.Bubble, typeof(ScrollBar));

    static ScrollBar()
    {
        AffectsRender<ScrollBar>(ValueProperty, MinimumProperty, MaximumProperty, ViewportSizeProperty, ThumbBrushProperty);
        AffectsMeasure<ScrollBar>(OrientationProperty);

        // :horizontal / :vertical select glyph + rail orientation styling (a control-semantic class
        // pair with no InteractionState bit — multi-class projection, CD30/C231).
        PseudoClassMapping.Register<ScrollBar, Orientation>(
            OrientationProperty,
            static o => o == Orientation.Horizontal ? ":horizontal" : ":vertical",
            ":horizontal", ":vertical");
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="MaximumProperty"/>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <inheritdoc cref="ViewportSizeProperty"/>
    public double ViewportSize { get => GetValue(ViewportSizeProperty); set => SetValue(ViewportSizeProperty, value); }

    /// <inheritdoc cref="SmallChangeProperty"/>
    public double SmallChange { get => GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, value); }

    /// <inheritdoc cref="LargeChangeProperty"/>
    public double LargeChange { get => GetValue(LargeChangeProperty); set => SetValue(LargeChangeProperty, value); }

    /// <inheritdoc cref="ThumbBrushProperty"/>
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }

    /// <summary>CLR sugar over <see cref="ScrollEvent"/>.</summary>
    public event EventHandler<ScrollEventArgs>? Scroll { add => AddHandler(ScrollEvent, value!); remove => RemoveHandler(ScrollEvent, value!); }

    /// <summary>The effective page step: <see cref="LargeChange"/> when set, else <see cref="ViewportSize"/>, else 1.</summary>
    private double EffectiveLargeChange
    {
        get
        {
            var large = LargeChange;
            if (large > 0)
                return large;
            var viewport = ViewportSize;
            return viewport > 0 ? viewport : 1;
        }
    }

    // ───────────────────────────── template wiring (CD17/§12.2) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The required Track wires itself to this bar via the internal ctor in the BuiltIn template;
        // the optional arrow RepeatButtons step ±SmallChange on each repeat (CD29/C234).
        _lineUp = GetTemplatePart<RepeatButton>(PartLineUp);
        _lineDown = GetTemplatePart<RepeatButton>(PartLineDown);

        if (_lineUp is { } up)
            up.Click += OnLineUp;
        if (_lineDown is { } down)
            down.Click += OnLineDown;
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_lineUp is { } up)
            up.Click -= OnLineUp;
        if (_lineDown is { } down)
            down.Click -= OnLineDown;
        _lineUp = null;
        _lineDown = null;
        base.OnTemplateDetaching(old);
    }

    private void OnLineUp(object? sender, ClickEventArgs e) => StepBy(-SmallChange, ScrollEventType.SmallDecrement);

    private void OnLineDown(object? sender, ClickEventArgs e) => StepBy(+SmallChange, ScrollEventType.SmallIncrement);

    // ───────────────────────────── value moves (track + drag + line) ─────────────────────────────

    /// <summary>Pages the value by <paramref name="direction"/> × <see cref="EffectiveLargeChange"/> (the track-click path, C233).</summary>
    internal void PageBy(int direction)
        => StepBy(direction * EffectiveLargeChange,
                  direction < 0 ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement);

    /// <summary>The thumb-drag value report (C233): cell-quantized, raised as <see cref="ScrollEventType.ThumbTrack"/>.</summary>
    internal void OnThumbDrag(double value) => SetValueAndRaise(value, ScrollEventType.ThumbTrack);

    /// <summary>The drag-start hook (no-op placeholder for future <c>ThumbPosition</c> semantics).</summary>
    internal void OnDragStart()
    {
    }

    /// <summary>The drag-end hook — raises one terminal <see cref="ScrollEventType.EndScroll"/> at the settled value.</summary>
    internal void OnDragEnd() => RaiseScroll(Value, ScrollEventType.EndScroll);

    private void StepBy(double delta, ScrollEventType type) => SetValueAndRaise(Value + delta, type);

    private void SetValueAndRaise(double rawValue, ScrollEventType type)
    {
        var clamped = Math.Clamp(rawValue, Minimum, Math.Max(Minimum, Maximum));
        if (clamped == Value)
            return;

        // Set the value without the OnValueChanged auto-raise (it would tag ThumbPosition), then raise
        // once with the action's own type.
        _suppressScrollEvent = true;
        try
        {
            Value = clamped;
        }
        finally
        {
            _suppressScrollEvent = false;
        }

        RaiseScroll(clamped, type);
    }

    /// <summary>Sets <see cref="Value"/> from the owner without re-raising the <see cref="Scroll"/> event (the two-way mirror back-path, CD28).</summary>
    internal void SetValueSilently(double value)
    {
        _suppressScrollEvent = true;
        try
        {
            Value = Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
        }
        finally
        {
            _suppressScrollEvent = false;
        }
    }

    private void RaiseScroll(double value, ScrollEventType type)
    {
        if (_suppressScrollEvent || !IsAttachedToTree)
            return;

        var args = new ScrollEventArgs(ScrollEvent, this, value, type);
        RaiseEvent(args);
    }

    // ───────────────────────────── coercion + change handlers ─────────────────────────────

    private static double CoerceValue(UIObject sender, double value)
        => sender is ScrollBar bar ? Math.Clamp(value, bar.Minimum, Math.Max(bar.Minimum, bar.Maximum)) : value;

    private static void OnValueChanged(UIObject sender, double oldValue, double newValue)
    {
        // A direct Value set (not via a Step/Page/drag) still raises a Scroll event so the owner's
        // mirror stays in sync — except when the owner originated it (SetValueSilently).
        if (sender is ScrollBar bar)
            bar.RaiseScroll(newValue, ScrollEventType.ThumbPosition);
    }

    private static void OnRangeChanged(UIObject sender, double oldValue, double newValue)
    {
        if (sender is ScrollBar bar)
            bar.CoerceValue(ValueProperty); // a range change re-clamps the value
    }

    private static void OnOrientationChanged(UIObject sender, Orientation oldValue, Orientation newValue)
    {
        if (sender is ScrollBar bar)
            bar.InvalidateMeasure();
    }
}
