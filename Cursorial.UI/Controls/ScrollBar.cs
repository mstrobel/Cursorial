using Cursorial.Drawing.Media;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Controls;

/// <summary>
/// A scroll bar (design doc §12.7, CD28): a 1-cell-wide (or 1-cell-tall) <see cref="Control"/> with a
/// <see cref="RangeBase.Value"/> ∈ <c>[Minimum, Maximum]</c> model, a proportional draggable thumb on a
/// <see cref="Track"/> rail, click-track paging (±<see cref="RangeBase.LargeChange"/>), and optional
/// line-step <see cref="RepeatButton"/>s (<c>PART_LineUpButton</c>/<c>PART_LineDownButton</c>) that
/// repeat ±<see cref="RangeBase.SmallChange"/> while held. <see cref="Orientation"/> (S1-owned) selects the
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
public class ScrollBar : RangeBase
{
    private const string PartTrack = "PART_Track";
    private const string PartLineUp = "PART_LineUpButton";
    private const string PartLineDown = "PART_LineDownButton";

    private RepeatButton? _lineUp;
    private RepeatButton? _lineDown;
    private Track? _track;
    private bool _suppressScrollEvent;     // set during a Value set whose owner re-raises with its own type

    /// <summary>The bar's axis (S1-owned <see cref="Controls.Orientation"/>; default <see cref="Orientation.Vertical"/>). <c>AffectsMeasure</c> + the <c>:horizontal</c>/<c>:vertical</c> classes.</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<ScrollBar, Orientation>(nameof(Orientation), defaultValue: Orientation.Vertical, changed: OnOrientationChanged);

    /// <summary>The viewport size in value units (the proportional thumb length input). <c>AffectsRender</c>.</summary>
    public static readonly StyledProperty<double> ViewportSizeProperty =
        UIProperty.Register<ScrollBar, double>(nameof(ViewportSize));

    /// <summary>The thumb fill brush (the proportional <c>█</c> run; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        UIProperty.Register<ScrollBar, IBrush?>(
            nameof(ThumbBrush),
            new PropertyMetadata<IBrush?>
            {
                DefaultResourceKey = ThemeKeys.ScrollBarThumbNormalBrush,
                Changed = (s, _, _) =>
                          {
                              if (s is not ScrollBar bar) return;
                              bar.InvalidateVisual();
                              bar._track?.UpdateBrush();
                          }
            });

    /// <summary>The bubbling scroll event raised whenever <see cref="RangeBase.Value"/> changes (<see cref="ScrollEventArgs"/>).</summary>
    public static readonly RoutedEvent<ScrollEventArgs> ScrollEvent =
        RoutedEvent<ScrollEventArgs>.Register(nameof(Scroll), RoutingStrategy.Bubble, typeof(ScrollBar));

    static ScrollBar()
    {
        // Min/Max/Value come from RangeBase (already AffectsRender there). ScrollBar overrides the inherited
        // defaults: Maximum 0 (no travel until the owner sets it) and LargeChange 0 (⇒ EffectiveLargeChange falls
        // back to ViewportSize) — both differ from RangeBase's WPF defaults of 1.
        MaximumProperty.OverrideDefaultValue<ScrollBar>(0);
        LargeChangeProperty.OverrideDefaultValue<ScrollBar>(0);

        AffectsRender<ScrollBar>(ViewportSizeProperty, ThumbBrushProperty);
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

    /// <inheritdoc cref="ViewportSizeProperty"/>
    public double ViewportSize { get => GetValue(ViewportSizeProperty); set => SetValue(ViewportSizeProperty, value); }

    /// <inheritdoc cref="ThumbBrushProperty"/>
    public IBrush? ThumbBrush { get => GetValue(ThumbBrushProperty); set => SetValue(ThumbBrushProperty, value); }

    /// <summary>CLR sugar over <see cref="ScrollEvent"/>.</summary>
    public event EventHandler<ScrollEventArgs>? Scroll { add => AddHandler(ScrollEvent, value!); remove => RemoveHandler(ScrollEvent, value!); }

    /// <summary>The effective page step: <see cref="RangeBase.LargeChange"/> when set, else <see cref="ViewportSize"/>, else 1.</summary>
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
        _track = GetTemplatePart<Track>(PartTrack);

        if (_lineUp is {} up)
            up.Click += OnLineUp;
        if (_lineDown is {} down)
            down.Click += OnLineDown;
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_lineUp is {} up)
            up.Click -= OnLineUp;
        if (_lineDown is {} down)
            down.Click -= OnLineDown;

        _lineUp = null;
        _lineDown = null;
        _track = null;

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

        // ReSharper disable once CompareOfFloatsByEqualityOperator
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

    /// <summary>Sets <see cref="RangeBase.Value"/> from the owner without re-raising the <see cref="Scroll"/> event (the two-way mirror back-path, CD28).</summary>
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

    // ───────────────────────────── change handlers ─────────────────────────────

    /// <summary>A value change (post-coercion) raises the <see cref="Scroll"/> event so the owner's mirror stays in
    /// sync — unless suppressed (a Step/Page/drag re-raises with its own type, or the owner originated it via
    /// <see cref="SetValueSilently"/>). Coercion + the range-change re-clamp are inherited from <see cref="RangeBase"/>.</summary>
    protected override void OnValueChanged(double oldValue, double newValue)
        => RaiseScroll(newValue, ScrollEventType.ThumbPosition);

    private static void OnOrientationChanged(UIObject sender, Orientation oldValue, Orientation newValue)
    {
        if (sender is ScrollBar bar)
            bar.InvalidateMeasure();
    }
}
