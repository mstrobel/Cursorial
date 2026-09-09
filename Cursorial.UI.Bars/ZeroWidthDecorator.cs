using Cursorial.Rendering;
using Cursorial.UI.Controls;

namespace Cursorial.UI.Bars;

internal sealed class ZeroWidthDecorator : Decorator
{
    /// <summary></summary>
    public static readonly StyledProperty<bool> IsZeroWidthProperty =
        UIProperty.Register<ZeroWidthDecorator, bool>(nameof(IsZeroWidth), defaultValue: true);

    /// <inheritdoc cref="IsZeroWidthProperty"/>
    public bool IsZeroWidth
    {
        get => GetValue(IsZeroWidthProperty);
        set => SetValue(IsZeroWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var measure = base.MeasureOverride(availableSize);
        if (IsZeroWidth is false) return measure;
        return new Size(0, measure.Rows);
    }
}