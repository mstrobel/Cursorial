namespace Cursorial.UI.Controls;

/// <summary>
/// An <see cref="ItemsControl"/> with a <see cref="Header"/> (design doc §12.6) — the shape <c>MenuItem</c>
/// derives from (a submenu is an items host with a header). Distinct from <see cref="HeaderedContentControl"/>
/// (the <c>ContentControl</c> variant for non-items headered controls); the design names two headered bases.
/// </summary>
public class HeaderedItemsControl : ItemsControl
{
    /// <summary>The header content (an access-key literal slot for <c>MenuItem</c>; AffectsMeasure; a
    /// UIElement value is logically adopted, like <c>ContentControl.Content</c>).</summary>
    public static readonly StyledProperty<object?> HeaderProperty =
        UIProperty.Register<HeaderedItemsControl, object?>(nameof(Header), changed: OnHeaderChanged);

    /// <summary>The template applied to the <see cref="Header"/> (null ⇒ the by-type <see cref="DataTemplate"/> chain).</summary>
    public static readonly StyledProperty<DataTemplate?> HeaderTemplateProperty =
        UIProperty.Register<HeaderedItemsControl, DataTemplate?>(nameof(HeaderTemplate));

    static HeaderedItemsControl() => AffectsMeasure<HeaderedItemsControl>(HeaderProperty, HeaderTemplateProperty);

    private static void OnHeaderChanged(UIObject sender, object? oldValue, object? newValue)
    {
        if (sender is not HeaderedItemsControl control)
            return;

        // Mirrors HeaderedContentControl.OnHeaderChanged (chain ③ / doc §12.3): an element-form
        // header must be a logical child — else DataContext doesn't inherit and name-scope walks
        // from inside it dead-end.
        if (oldValue is UIElement oldElement && ReferenceEquals(oldElement.LogicalParent, control))
            control.RemoveLogicalChild(oldElement);

        if (newValue is UIElement newElement && newElement.LogicalParent is null)
            control.AddLogicalChild(newElement);
    }

    /// <inheritdoc cref="HeaderProperty"/>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    /// <inheritdoc cref="HeaderTemplateProperty"/>
    public DataTemplate? HeaderTemplate { get => GetValue(HeaderTemplateProperty); set => SetValue(HeaderTemplateProperty, value); }
}
