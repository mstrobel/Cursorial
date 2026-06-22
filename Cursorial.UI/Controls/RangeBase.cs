namespace Cursorial.UI.Controls;

/// <summary>
/// The shared base for controls with a <see cref="Value"/> in <c>[Minimum, Maximum]</c> (the WPF/Avalonia
/// <c>RangeBase</c>): <see cref="ScrollBar"/>, <see cref="ProgressBar"/>, <see cref="Slider"/>. It owns the three
/// range properties with value coercion (<see cref="Value"/> is clamped into the range, and a range change
/// re-clamps it) plus the <see cref="SmallChange"/>/<see cref="LargeChange"/> step deltas. Derived controls observe
/// changes through the <see cref="OnMinimumChanged"/>/<see cref="OnMaximumChanged"/>/<see cref="OnValueChanged"/>
/// virtuals (zero-allocation — there is deliberately no bubbling <c>ValueChanged</c> event here, so a high-frequency
/// value producer like <see cref="ScrollBar"/> stays allocation-free; a control that wants a public event raises its
/// own from the virtual).
/// </summary>
public abstract class RangeBase : Control
{
    /// <summary>The lower bound of <see cref="Value"/> (default 0; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<double> MinimumProperty =
        UIProperty.Register<RangeBase, double>(nameof(Minimum), defaultValue: 0, changed: OnMinimumChangedCore);

    /// <summary>The upper bound of <see cref="Value"/> (default 1; <c>AffectsRender</c>). Derived controls override
    /// the default (ScrollBar ⇒ 0, ProgressBar ⇒ 100).</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        UIProperty.Register<RangeBase, double>(nameof(Maximum), defaultValue: 1, changed: OnMaximumChangedCore);

    /// <summary>The current value, coerced into <c>[Minimum, max(Minimum, Maximum)]</c> (default 0; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<double> ValueProperty =
        UIProperty.Register<RangeBase, double>(nameof(Value), defaultValue: 0, coerce: CoerceValueCore, changed: OnValueChangedCore);

    /// <summary>The line-step delta (arrow keys / repeat buttons; default 1).</summary>
    public static readonly StyledProperty<double> SmallChangeProperty =
        UIProperty.Register<RangeBase, double>(nameof(SmallChange), defaultValue: 1.0);

    /// <summary>The page-step delta (PageUp/PageDown / track click; default 1). <see cref="ScrollBar"/> overrides
    /// the default to 0 so it falls back to its viewport size.</summary>
    public static readonly StyledProperty<double> LargeChangeProperty =
        UIProperty.Register<RangeBase, double>(nameof(LargeChange), defaultValue: 1.0);

    static RangeBase() => AffectsRender<RangeBase>(MinimumProperty, MaximumProperty, ValueProperty);

    /// <inheritdoc cref="MinimumProperty"/>
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="MaximumProperty"/>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="SmallChangeProperty"/>
    public double SmallChange { get => GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, value); }

    /// <inheritdoc cref="LargeChangeProperty"/>
    public double LargeChange { get => GetValue(LargeChangeProperty); set => SetValue(LargeChangeProperty, value); }

    /// <summary>The filled fraction <c>(Value − Minimum) / (Maximum − Minimum)</c> in <c>[0, 1]</c> (0 when the range
    /// is empty or inverted).</summary>
    public double FilledFraction
    {
        get
        {
            var range = Maximum - Minimum;
            return range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        }
    }

    /// <summary>Called after <see cref="Minimum"/> changes (the value has already been re-clamped).</summary>
    protected virtual void OnMinimumChanged(double oldValue, double newValue) { }

    /// <summary>Called after <see cref="Maximum"/> changes (the value has already been re-clamped).</summary>
    protected virtual void OnMaximumChanged(double oldValue, double newValue) { }

    /// <summary>Called after <see cref="Value"/> changes (post-coercion).</summary>
    protected virtual void OnValueChanged(double oldValue, double newValue) { }

    private static double CoerceValueCore(UIObject sender, double value)
    {
        var range = (RangeBase)sender;
        return Math.Clamp(value, range.Minimum, Math.Max(range.Minimum, range.Maximum));
    }

    private static void OnMinimumChangedCore(UIObject sender, double oldValue, double newValue)
    {
        var range = (RangeBase)sender;
        range.CoerceValue(ValueProperty); // a range change re-clamps the value
        range.OnMinimumChanged(oldValue, newValue);
    }

    private static void OnMaximumChangedCore(UIObject sender, double oldValue, double newValue)
    {
        var range = (RangeBase)sender;
        range.CoerceValue(ValueProperty);
        range.OnMaximumChanged(oldValue, newValue);
    }

    private static void OnValueChangedCore(UIObject sender, double oldValue, double newValue)
        => ((RangeBase)sender).OnValueChanged(oldValue, newValue);
}
