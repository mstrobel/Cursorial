using Cursorial.Rendering;

namespace Cursorial.UI;

/// <summary>
/// Docks children against its edges in child order (design doc §5.4; the classic WPF
/// accumulate-from-edges algorithm). A docked child's slot extent on its dock axis is the child's
/// <b>desired size</b> — it may overflow the panel once space is exhausted (LD15); remainders clamp
/// ≥ 0, so exhausted space yields zero-extent slots, never negative rects. When
/// <see cref="LastChildFill"/> is true (the default) the final child receives the remaining rect.
/// </summary>
public class DockPanel : Panel
{
    /// <summary>Whether the last child fills the remaining space instead of docking (default true). <c>[AffectsMeasure]</c></summary>
    public static readonly StyledProperty<bool> LastChildFillProperty =
        UIProperty.Register<DockPanel, bool>(nameof(LastChildFill), defaultValue: true);

    /// <summary>
    /// The edge a child docks against (default <see cref="Dock.Left"/>).
    /// <c>[AffectsParentMeasure]</c> — carried on the property-global effects lane, so the flag
    /// fires on host types whose frozen per-type tables never saw this registration (doc §5.5).
    /// </summary>
    public static readonly AttachedProperty<Dock> DockProperty =
        UIProperty.RegisterAttached<DockPanel, UIElement, Dock>("Dock", defaultValue: Dock.Left);

    static DockPanel()
    {
        AffectsMeasure<DockPanel>(LastChildFillProperty);
        AffectsParentMeasure<DockPanel>(DockProperty);
    }

    /// <inheritdoc cref="LastChildFillProperty"/>
    public bool LastChildFill { get => GetValue(LastChildFillProperty); set => SetValue(LastChildFillProperty, value); }

    /// <summary>Reads <see cref="DockProperty"/> for <paramref name="element"/>.</summary>
    public static Dock GetDock(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(DockProperty);
    }

    /// <summary>Writes <see cref="DockProperty"/> for <paramref name="element"/>.</summary>
    public static void SetDock(UIElement element, Dock value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(DockProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // WPF formula (matrix §7 preamble): each child is measured with the constraint minus the
        // accumulated docked desire per axis; the panel's desired accumulates the docked axis and
        // maxes the perpendicular.
        var accumulatedWidth = 0;
        var accumulatedHeight = 0;
        var parallelWidth = 0;
        var parallelHeight = 0;

        var children = Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Measure(new Size(
                LayoutMath.Sub(availableSize.Columns, accumulatedWidth),
                LayoutMath.Sub(availableSize.Rows, accumulatedHeight)));

            var desired = child.DesiredSize;
            switch (GetDock(child))
            {
                case Dock.Left:
                case Dock.Right:
                    parallelHeight = Math.Max(parallelHeight, LayoutMath.Add(accumulatedHeight, desired.Rows));
                    accumulatedWidth = LayoutMath.Add(accumulatedWidth, desired.Columns);
                    break;
                default:
                    parallelWidth = Math.Max(parallelWidth, LayoutMath.Add(accumulatedWidth, desired.Columns));
                    accumulatedHeight = LayoutMath.Add(accumulatedHeight, desired.Rows);
                    break;
            }
        }

        return new Size(Math.Max(parallelWidth, accumulatedWidth), Math.Max(parallelHeight, accumulatedHeight));
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var accumulatedLeft = 0;
        var accumulatedTop = 0;
        var accumulatedRight = 0;
        var accumulatedBottom = 0;

        var children = Children;
        var fillIndex = LastChildFill ? children.Count - 1 : -1;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var remainingWidth = Math.Max(0, finalSize.Columns - accumulatedLeft - accumulatedRight);
            var remainingHeight = Math.Max(0, finalSize.Rows - accumulatedTop - accumulatedBottom);

            if (i == fillIndex)
            {
                child.Arrange(new Rect(accumulatedLeft, accumulatedTop, remainingWidth, remainingHeight));
                continue;
            }

            var desired = child.DesiredSize;
            Rect slot;
            switch (GetDock(child))
            {
                case Dock.Left:
                    // Slot extent on the dock axis = desired, even past exhaustion (LD15).
                    slot = new Rect(accumulatedLeft, accumulatedTop, desired.Columns, remainingHeight);
                    accumulatedLeft += desired.Columns;
                    break;
                case Dock.Top:
                    slot = new Rect(accumulatedLeft, accumulatedTop, remainingWidth, desired.Rows);
                    accumulatedTop += desired.Rows;
                    break;
                case Dock.Right:
                    slot = new Rect(
                        accumulatedLeft + Math.Max(0, remainingWidth - desired.Columns), accumulatedTop,
                        desired.Columns, remainingHeight);
                    accumulatedRight += desired.Columns;
                    break;
                default:
                    slot = new Rect(
                        accumulatedLeft, accumulatedTop + Math.Max(0, remainingHeight - desired.Rows),
                        remainingWidth, desired.Rows);
                    accumulatedBottom += desired.Rows;
                    break;
            }

            child.Arrange(slot);
        }

        return finalSize;
    }
}
