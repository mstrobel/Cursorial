namespace Cursorial.UI;

/// <summary>
/// The S2 data-binding face of <see cref="UIElement"/> (design doc §6.4): the inherited
/// <c>DataContext</c> styled property — binding's anchoring backbone — and the template-aware
/// <see cref="FindName"/> lookup (S2-owned).
/// </summary>
public partial class UIElement
{
    /// <summary>
    /// The inherited binding-source context (design doc §6.4). An ordinary inherited styled
    /// property — Fork A's lazy-read/eager-notify inheritance gives binding its backbone for free. A
    /// default-source binding anchors on the target's <c>DataContext</c>; changing it rebinds the
    /// whole subtree's default-source bindings (BD3).
    /// </summary>
    public static readonly StyledProperty<object?> DataContextProperty =
        UIProperty.Register<UIElement, object?>(nameof(DataContext), defaultValue: null, inherits: true);

    /// <inheritdoc cref="DataContextProperty"/>
    public object? DataContext
    {
        get => GetValue(DataContextProperty);
        set => SetValue(DataContextProperty, value);
    }

    /// <summary>
    /// The template-aware name lookup (design doc §6.3 — S2 owns this):
    /// <c>NameScope.FindEnclosing(this)?.Find(name)</c>. Consumed by S5 storyboard targeting and app
    /// code (<c>window.FindName("toast")</c>). Returns <see langword="null"/> on a miss (not a throw).
    /// </summary>
    public object? FindName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return NameScope.FindEnclosing(this)?.Find(name);
    }
}
