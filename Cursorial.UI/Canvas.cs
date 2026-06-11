using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// Positions children at explicit coordinates via the <see cref="LeftProperty"/> /
/// <see cref="TopProperty"/> / <see cref="RightProperty"/> / <see cref="BottomProperty"/> attached
/// properties (design doc §5.4). Children measure with an unbounded constraint; the canvas itself
/// desires 0×0. <c>Left</c> beats <c>Right</c> and <c>Top</c> beats <c>Bottom</c> when both are set
/// (LD16); computed offsets clamp ≥ 0 — negative placement never enters layout (use
/// <c>RenderOffset*</c> for negative slides, doc §5.2). All four attached properties are
/// <c>[AffectsParentArrange]</c> — the cheap, arrange-only invalidation lane.
/// </summary>
public class Canvas : Panel
{
    /// <summary>The child's column offset from the canvas's left edge. <c>[AffectsParentArrange]</c>; beats <see cref="RightProperty"/> (LD16).</summary>
    public static readonly AttachedProperty<int?> LeftProperty =
        UIProperty.RegisterAttached<Canvas, UIElement, int?>("Left");

    /// <summary>The child's row offset from the canvas's top edge. <c>[AffectsParentArrange]</c>; beats <see cref="BottomProperty"/> (LD16).</summary>
    public static readonly AttachedProperty<int?> TopProperty =
        UIProperty.RegisterAttached<Canvas, UIElement, int?>("Top");

    /// <summary>The child's column offset from the canvas's right edge. <c>[AffectsParentArrange]</c></summary>
    public static readonly AttachedProperty<int?> RightProperty =
        UIProperty.RegisterAttached<Canvas, UIElement, int?>("Right");

    /// <summary>The child's row offset from the canvas's bottom edge. <c>[AffectsParentArrange]</c></summary>
    public static readonly AttachedProperty<int?> BottomProperty =
        UIProperty.RegisterAttached<Canvas, UIElement, int?>("Bottom");

    static Canvas()
    {
        AffectsParentArrange<Canvas>(LeftProperty, TopProperty, RightProperty, BottomProperty);
    }

    /// <summary>Reads <see cref="LeftProperty"/>.</summary>
    public static int? GetLeft(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(LeftProperty);
    }

    /// <summary>Writes <see cref="LeftProperty"/>.</summary>
    public static void SetLeft(UIElement element, int? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LeftProperty, value);
    }

    /// <summary>Reads <see cref="TopProperty"/>.</summary>
    public static int? GetTop(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(TopProperty);
    }

    /// <summary>Writes <see cref="TopProperty"/>.</summary>
    public static void SetTop(UIElement element, int? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(TopProperty, value);
    }

    /// <summary>Reads <see cref="RightProperty"/>.</summary>
    public static int? GetRight(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(RightProperty);
    }

    /// <summary>Writes <see cref="RightProperty"/>.</summary>
    public static void SetRight(UIElement element, int? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RightProperty, value);
    }

    /// <summary>Reads <see cref="BottomProperty"/>.</summary>
    public static int? GetBottom(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(BottomProperty);
    }

    /// <summary>Writes <see cref="BottomProperty"/>.</summary>
    public static void SetBottom(UIElement element, int? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BottomProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(LayoutMath.Unbounded, LayoutMath.Unbounded);
        var children = Children;
        for (var i = 0; i < children.Count; i++)
            children[i].Measure(unbounded);

        return Size.Empty; // zero desired contribution regardless of children (WPF)
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var desired = child.DesiredSize;

            var column = GetLeft(child)
                ?? (GetRight(child) is { } right ? finalSize.Columns - right - desired.Columns : 0);
            var row = GetTop(child)
                ?? (GetBottom(child) is { } bottom ? finalSize.Rows - bottom - desired.Rows : 0);

            // Negative placement never enters layout (doc §5.2) — use RenderOffset* instead.
            child.Arrange(new Rect(Math.Max(0, column), Math.Max(0, row), desired.Columns, desired.Rows));
        }

        return finalSize;
    }
}
