using Cursorial.Rendering;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// Args for <see cref="ScrollViewer.ScrollChanged"/> (WPF/Avalonia parity): the settled scroll
/// geometry after a change — the new cell offsets, the current <see cref="Extent"/>/<see cref="Viewport"/>
/// sizes, and the per-axis cell deltas from the prior offsets.
/// </summary>
public sealed class ScrollChangedEventArgs : RoutedEventArgs
{
    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public ScrollChangedEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    public ScrollChangedEventArgs(RoutedEvent<ScrollChangedEventArgs> routedEvent, UIElement source,
                                  int horizontalOffset, int verticalOffset, Size extent, Size viewport,
                                  int horizontalChange, int verticalChange)
        : base(routedEvent, source)
    {
        HorizontalOffset = horizontalOffset;
        VerticalOffset = verticalOffset;
        Extent = extent;
        Viewport = viewport;
        HorizontalChange = horizontalChange;
        VerticalChange = verticalChange;
    }

    /// <summary>The new horizontal scroll offset in cells (<see cref="ScrollViewer.HorizontalOffset"/>).</summary>
    public int HorizontalOffset { get; init; }

    /// <summary>The new vertical scroll offset in cells (<see cref="ScrollViewer.VerticalOffset"/>).</summary>
    public int VerticalOffset { get; init; }

    /// <summary>The scrollable content size in cells (<see cref="ScrollViewer.Extent"/>).</summary>
    public Size Extent { get; init; }

    /// <summary>The visible content size in cells (<see cref="ScrollViewer.Viewport"/>).</summary>
    public Size Viewport { get; init; }

    /// <summary>The change in <see cref="HorizontalOffset"/> from the previous value, in cells.</summary>
    public int HorizontalChange { get; init; }

    /// <summary>The change in <see cref="VerticalOffset"/> from the previous value, in cells.</summary>
    public int VerticalChange { get; init; }
}
