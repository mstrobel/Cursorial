using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A value picker on a rail (the WPF/Avalonia <c>Slider</c>): a <see cref="RangeBase"/> whose draggable
/// <see cref="Thumb"/> (<c>PART_Thumb</c>) rides a 1-cell rail, positioned at the <see cref="RangeBase.Value"/>
/// fraction. <see cref="Orientation"/> selects the axis — horizontal maps Minimum→left, Maximum→right; vertical maps
/// Minimum→bottom, Maximum→top (WPF convention). Dragging the thumb, clicking the rail (jumps the value to the click),
/// and the keyboard (←/→ or ↓/↑ ±<see cref="RangeBase.SmallChange"/>, PageUp/PageDown ±<see cref="RangeBase.LargeChange"/>,
/// Home/End → Minimum/Maximum) all move the value. The rail is drawn by <see cref="Render"/>; the thumb (the template
/// root) is arranged on top.
/// </summary>
[TemplatePart(PartThumb, typeof(Thumb), IsRequired = true)]
public class Slider : RangeBase
{
    private const string PartThumb = "PART_Thumb";
    private const int ThumbExtent = 1; // the thumb is one cell along the long axis (a marker)

    private Thumb? _thumb;
    private double _dragStartValue;
    private int _dragTravel;

    /// <summary>The slider's axis (default <see cref="Orientation.Horizontal"/>). <c>AffectsMeasure</c> + the
    /// <c>:horizontal</c>/<c>:vertical</c> classes.</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        UIProperty.Register<Slider, Orientation>(nameof(Orientation), defaultValue: Orientation.Horizontal, changed: OnOrientationChanged);

    /// <summary>The FILLED (value-side) rail pen — the design guide's accent <c>━</c> from the start to the thumb
    /// (the unfilled remainder uses <see cref="Control.BorderPen"/>). <c>AffectsRender</c>.</summary>
    public static readonly StyledProperty<Pen?> FilledPenProperty =
        UIProperty.Register<Slider, Pen?>(nameof(FilledPen));

    /// <summary>The bubbling event raised whenever <see cref="RangeBase.Value"/> changes (<see cref="RangeValueChangedEventArgs"/>).
    /// Owned by <see cref="Slider"/>, not <see cref="RangeBase"/> — the shared range base stays allocation-free (see its remarks).</summary>
    public static readonly RoutedEvent<RangeValueChangedEventArgs> ValueChangedEvent =
        RoutedEvent<RangeValueChangedEventArgs>.Register(nameof(ValueChanged), RoutingStrategy.Bubble, typeof(Slider));

    static Slider()
    {
        FocusableProperty.OverrideDefaultValue<Slider>(true); // an interactive value picker takes keyboard focus
        AffectsMeasure<Slider>(OrientationProperty);
        AffectsArrange<Slider>(ValueProperty, MinimumProperty, MaximumProperty);
        // The two-tone rail's filled/empty split moves with the value, so the track re-renders on a value/range/pen change.
        AffectsRender<Slider>(ValueProperty, MinimumProperty, MaximumProperty, OrientationProperty, FilledPenProperty);

        PseudoClassMapping.Register<Slider, Orientation>(
            OrientationProperty,
            static o => o == Orientation.Horizontal ? ":horizontal" : ":vertical",
            ":horizontal", ":vertical");
    }

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation { get => GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    /// <inheritdoc cref="FilledPenProperty"/>
    public Pen? FilledPen { get => GetValue(FilledPenProperty); set => SetValue(FilledPenProperty, value); }

    /// <summary>CLR sugar over <see cref="ValueChangedEvent"/>.</summary>
    public event EventHandler<RangeValueChangedEventArgs>? ValueChanged { add => AddHandler(ValueChangedEvent, value!); remove => RemoveHandler(ValueChangedEvent, value!); }

    private bool Vertical => Orientation == Orientation.Vertical;

    // ───────────────────────────── template wiring ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _thumb = GetTemplatePart<Thumb>(PartThumb);
        if (_thumb is { } thumb)
        {
            thumb.DragStarted += OnThumbDragStarted;
            thumb.DragDelta += OnThumbDragDelta;
        }
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        if (_thumb is { } thumb)
        {
            thumb.DragStarted -= OnThumbDragStarted;
            thumb.DragDelta -= OnThumbDragDelta;
        }
        _thumb = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── layout (position the thumb by Value) ─────────────────────────────

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        _thumb?.Measure(Vertical ? new Size(availableSize.Columns, ThumbExtent) : new Size(ThumbExtent, availableSize.Rows));
        // Stretch along the axis, one cell across (the consumer sizes the long axis via Width/Height).
        return Vertical ? new Size(1, 0) : new Size(0, 1);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_thumb is { } thumb)
        {
            var length = Vertical ? finalSize.Rows : finalSize.Columns;
            var travel = Math.Max(0, length - ThumbExtent);
            var fraction = FilledFraction;
            if (Vertical)
            {
                // Minimum at the bottom, Maximum at the top.
                var y = (int)Math.Round((1 - fraction) * travel);
                thumb.Arrange(new Rect(0, y, ThumbExtent, ThumbExtent));
            }
            else
            {
                var x = (int)Math.Round(fraction * travel);
                thumb.Arrange(new Rect(x, 0, ThumbExtent, ThumbExtent));
            }
        }

        return Vertical ? new Size(ThumbExtent, finalSize.Rows) : new Size(finalSize.Columns, ThumbExtent);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEmpty)
            return;

        // Two heavy ━ segments split at the thumb (design guide): the FILLED (value) side in FilledPen, the empty
        // remainder in BorderPen. The thumb child renders its marker on top at the split. Either pen may be null.
        var size = context.Size;
        var filled = FilledPen;
        var empty = BorderPen;
        var fraction = FilledFraction;

        if (Vertical)
        {
            var x = size.Columns / 2;
            var travel = Math.Max(0, size.Rows - ThumbExtent);
            var thumbY = (int)Math.Round((1 - fraction) * travel); // Minimum at the bottom
            if (empty is { } e && thumbY > 0)
                context.DrawLine(x, 0, x, thumbY - 1, e, overwrite: true, armHint: Arm.Down);                 // empty: top → thumb
            if (filled is { } f && thumbY < size.Rows - 1)
                context.DrawLine(x, thumbY + 1, x, size.Rows - 1, f, overwrite: true, armHint: Arm.Down);     // filled: thumb → bottom
        }
        else
        {
            var y = size.Rows / 2;
            var travel = Math.Max(0, size.Columns - ThumbExtent);
            var thumbX = (int)Math.Round(fraction * travel);
            if (filled is { } f && thumbX > 0)
                context.DrawLine(0, y, thumbX - 1, y, f, overwrite: true, armHint: Arm.Right);                // filled: left → thumb
            if (empty is { } e && thumbX < size.Columns - 1)
                context.DrawLine(thumbX + 1, y, size.Columns - 1, y, e, overwrite: true, armHint: Arm.Right); // empty: thumb → right
        }
    }

    // ───────────────────────────── thumb drag ─────────────────────────────

    private void OnThumbDragStarted(object? sender, ThumbDragEventArgs e)
    {
        _dragStartValue = Value;
        var length = Vertical ? Bounds.Size.Rows : Bounds.Size.Columns;
        _dragTravel = Math.Max(1, length - ThumbExtent);
    }

    private void OnThumbDragDelta(object? sender, ThumbDragEventArgs e)
    {
        var range = Maximum - Minimum;
        if (range <= 0)
            return;

        // Horizontal: +H increases. Vertical: -V increases (up = Maximum).
        var axisDelta = Vertical ? -e.VerticalChange : e.HorizontalChange;
        Value = _dragStartValue + range * axisDelta / _dragTravel; // RangeBase coercion clamps
    }

    // ───────────────────────────── rail click + keyboard ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        // A click on the rail (the thumb captured its own clicks) jumps the value to the click position.
        var position = e.GetPosition(this);
        var length = Vertical ? Bounds.Size.Rows : Bounds.Size.Columns;
        var travel = Math.Max(1, length - ThumbExtent);
        var coord = Vertical ? position.Row : position.Column;
        var fraction = Math.Clamp((double)coord / travel, 0, 1);
        if (Vertical)
            fraction = 1 - fraction; // top = Maximum

        Value = Minimum + (Maximum - Minimum) * fraction; // coercion clamps
        Focus();
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Modifiers != KeyModifiers.None)
            return;

        var handled = true;
        switch (e.Key)
        {
            case Key.LeftArrow when !Vertical:
            case Key.DownArrow when Vertical:
                Value -= SmallChange;
                break;
            case Key.RightArrow when !Vertical:
            case Key.UpArrow when Vertical:
                Value += SmallChange;
                break;
            case Key.PageUp:
                Value += LargeChange;
                break;
            case Key.PageDown:
                Value -= LargeChange;
                break;
            case Key.Home:
                Value = Minimum;
                break;
            case Key.End:
                Value = Maximum;
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
            e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnValueChanged(double oldValue, double newValue)
    {
        InvalidateArrange();
        // The thumb re-positions above; the value producer (drag/click/keyboard/binding) also gets a public
        // bubbling notification. Slider-owned, payload-carrying ⇒ caller-owned args (new, never RentEvent).
        RaiseEvent(new RangeValueChangedEventArgs(ValueChangedEvent, this, oldValue, newValue));
    }

    private static void OnOrientationChanged(UIObject sender, Orientation oldValue, Orientation newValue)
        => ((Slider)sender).InvalidateMeasure();
}
