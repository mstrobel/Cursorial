using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>The visibility policy of a <see cref="ScrollViewer"/> scrollbar on one axis (design doc §12.7).</summary>
public enum ScrollBarVisibility
{
    /// <summary>The bar is shown only when the content overflows the viewport on that axis (the re-measure loop, CD28).</summary>
    Auto,

    /// <summary>The bar is never shown but the axis is still scrollable by wheel / keyboard.</summary>
    Hidden,

    /// <summary>The bar is never shown and the axis cannot scroll (offset coerced to 0; the constraint passes through — L203).</summary>
    Disabled,

    /// <summary>The bar is always shown.</summary>
    Visible
}

/// <summary>The kind of scroll action a <see cref="ScrollBar"/> requested (design doc §12.7).</summary>
public enum ScrollEventType
{
    /// <summary>A small (line) decrease — a line-up/left RepeatButton.</summary>
    SmallDecrement,

    /// <summary>A small (line) increase — a line-down/right RepeatButton.</summary>
    SmallIncrement,

    /// <summary>A large (page) decrease — a track click above/left of the thumb.</summary>
    LargeDecrement,

    /// <summary>A large (page) increase — a track click below/right of the thumb.</summary>
    LargeIncrement,

    /// <summary>An absolute value from a thumb drag.</summary>
    ThumbPosition,

    /// <summary>A drag in progress (live thumb tracking).</summary>
    ThumbTrack,

    /// <summary>The drag ended.</summary>
    EndScroll
}

/// <summary>Args for <see cref="ScrollBar.Scroll"/> (design doc §12.7): the requested new value and the action that produced it.</summary>
public sealed class ScrollEventArgs : RoutedEventArgs
{
    /// <summary>Creates an empty caller-owned args (also the pooled construction path).</summary>
    public ScrollEventArgs()
    {
    }

    /// <summary>Creates a caller-owned args ready to raise.</summary>
    public ScrollEventArgs(RoutedEvent<ScrollEventArgs> routedEvent, UIElement source, double newValue, ScrollEventType scrollEventType)
        : base(routedEvent, source)
    {
        NewValue = newValue;
        ScrollEventType = scrollEventType;
    }

    /// <summary>The requested new <see cref="RangeBase.Value"/>.</summary>
    public double NewValue { get; init; }

    /// <summary>The action that produced the scroll.</summary>
    public ScrollEventType ScrollEventType { get; init; }
}
