using Cursorial.Rendering;

namespace Cursorial.UI.Controls;

/// <summary>
/// A single-child layout passthrough (design doc §12.7): hosts one <see cref="Child"/> and, by
/// default, measures/arranges it 1:1. The base for <see cref="Border"/>. Not a <see cref="Control"/> —
/// a decorator has no template.
/// </summary>
public class Decorator : UIElement
{
    private UIElement? _child;

    /// <summary>The decorated child (a logical + visual child). Setting it re-parents the previous child out.</summary>
    public UIElement? Child
    {
        get => _child;
        set
        {
            VerifyAccess();
            if (ReferenceEquals(_child, value))
                return;

            if (_child is { } old)
                DisownChild(old);

            _child = value;

            if (value is not null)
                AdoptChild(value, index: -1);

            InvalidateMeasure();
        }
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_child is null)
            return Size.Empty;

        _child.Measure(availableSize);
        return _child.DesiredSize;
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        _child?.Arrange(new Rect(0, 0, finalSize.Columns, finalSize.Rows));
        return finalSize;
    }
}
