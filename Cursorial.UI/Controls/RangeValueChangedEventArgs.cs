using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// Args for <see cref="Slider.ValueChanged"/> (design doc §12.7): the <see cref="RangeBase.Value"/> before and after
/// the change. The event is raised at the <see cref="Slider"/> level — deliberately NOT at <see cref="RangeBase"/> —
/// so the shared range base stays allocation-free for high-frequency value producers like <see cref="ScrollBar"/>
/// (see <see cref="RangeBase"/> remarks); a control that wants a public event raises its own from the virtual.
/// </summary>
public sealed class RangeValueChangedEventArgs : RoutedEventArgs
{
    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public RangeValueChangedEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    public RangeValueChangedEventArgs(RoutedEvent<RangeValueChangedEventArgs> routedEvent, UIElement source, double oldValue, double newValue)
        : base(routedEvent, source)
    {
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>The <see cref="RangeBase.Value"/> before the change.</summary>
    public double OldValue { get; init; }

    /// <summary>The <see cref="RangeBase.Value"/> after the change (post-coercion).</summary>
    public double NewValue { get; init; }
}
