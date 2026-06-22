using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A draggable primitive (the WPF/Avalonia <c>Thumb</c>): on a left-button press it captures the mouse and reports
/// the drag as <see cref="DragStarted"/> → <see cref="DragDelta"/>* → <see cref="DragCompleted"/>. The
/// <see cref="ThumbDragEventArgs.HorizontalChange"/>/<see cref="ThumbDragEventArgs.VerticalChange"/> are the
/// <b>cumulative</b> cell change from the grab point, measured in stable terminal (screen) coordinates — so they stay
/// correct even as the thumb (or its container) moves underneath the pointer during the drag. The thumb does not move
/// itself; a consumer (<see cref="Slider"/>, <c>GridSplitter</c>) interprets the deltas. It paints its
/// <see cref="Control.Background"/> when set and otherwise stretches to fill whatever space it is given.
/// </summary>
public class Thumb : Control
{
    private bool _dragging;
    private CellPosition _origin; // the pointer's terminal position at the grab (the cumulative-delta anchor)

    /// <summary>Raised (bubbling) when a drag begins (the deltas are 0).</summary>
    public static readonly RoutedEvent<ThumbDragEventArgs> DragStartedEvent =
        RoutedEvent<ThumbDragEventArgs>.Register(nameof(DragStarted), RoutingStrategy.Bubble, typeof(Thumb));

    /// <summary>Raised (bubbling) on each move during a drag with the cumulative change from the grab point.</summary>
    public static readonly RoutedEvent<ThumbDragEventArgs> DragDeltaEvent =
        RoutedEvent<ThumbDragEventArgs>.Register(nameof(DragDelta), RoutingStrategy.Bubble, typeof(Thumb));

    /// <summary>Raised (bubbling) when the drag ends (release) or is canceled (capture lost: detach / surface close /
    /// terminal focus-out). On release it carries the final cumulative change; on cancel the change is 0.</summary>
    public static readonly RoutedEvent<ThumbDragEventArgs> DragCompletedEvent =
        RoutedEvent<ThumbDragEventArgs>.Register(nameof(DragCompleted), RoutingStrategy.Bubble, typeof(Thumb));

    /// <summary>CLR sugar over <see cref="DragStartedEvent"/>.</summary>
    public event EventHandler<ThumbDragEventArgs>? DragStarted { add => AddHandler(DragStartedEvent, value!); remove => RemoveHandler(DragStartedEvent, value!); }

    /// <summary>CLR sugar over <see cref="DragDeltaEvent"/>.</summary>
    public event EventHandler<ThumbDragEventArgs>? DragDelta { add => AddHandler(DragDeltaEvent, value!); remove => RemoveHandler(DragDeltaEvent, value!); }

    /// <summary>CLR sugar over <see cref="DragCompletedEvent"/>.</summary>
    public event EventHandler<ThumbDragEventArgs>? DragCompleted { add => AddHandler(DragCompletedEvent, value!); remove => RemoveHandler(DragCompletedEvent, value!); }

    /// <summary>Whether a drag is in progress (the mouse is captured).</summary>
    public bool IsDragging => _dragging;

    /// <summary>The thumb has no intrinsic size — it fills whatever its container (a Slider track / GridSplitter
    /// cell) gives it.</summary>
    protected override Size MeasureOverride(Size availableSize) => Size.Empty;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (Background is { } background && !context.Bounds.IsEmpty)
            context.FillRectangle(new Rect(0, 0, context.Size.Columns, context.Size.Rows), background);
    }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        // Ignore a press while already dragging: capture routes EVERY ButtonDown to the holder, so without this a
        // rapid second press (or a duplicated wire event) would re-anchor _origin and raise a second DragStarted with
        // no DragCompleted between — corrupting all later deltas (WPF's Thumb ignores presses while IsDragging).
        if (e.Handled || e.Button != MouseButton.Left || _dragging)
            return;

        e.Handled = true;
        if (!CaptureMouse())
            return;

        _dragging = true;
        _origin = e.ScreenPosition;
        Raise(DragStartedEvent, 0, 0);
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        var position = e.ScreenPosition;
        Raise(DragDeltaEvent, position.Column - _origin.Column, position.Row - _origin.Row);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left || !_dragging)
            return;

        e.Handled = true;
        var position = e.ScreenPosition;

        // Clear the flag BEFORE releasing capture: ReleaseMouseCapture synchronously raises LostMouseCapture, whose
        // handler would otherwise re-complete the drag (a double DragCompleted). This is the correct ordering — Track
        // historically had it backwards (clearing after release) and double-raised its EndScroll.
        _dragging = false;
        ReleaseMouseCapture();
        Raise(DragCompletedEvent, position.Column - _origin.Column, position.Row - _origin.Row);
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging)
            return;

        // A non-release capture loss (detach / surface close / terminal focus-out) cancels the drag — no final delta.
        _dragging = false;
        Raise(DragCompletedEvent, 0, 0);
    }

    private void Raise(RoutedEvent<ThumbDragEventArgs> routedEvent, int horizontalChange, int verticalChange)
    {
        if (IsAttachedToTree)
            RaiseEvent(new ThumbDragEventArgs(routedEvent, this, horizontalChange, verticalChange));
    }
}

/// <summary>Args for <see cref="Thumb"/>'s drag events — the cumulative cell change from the grab point.</summary>
public sealed class ThumbDragEventArgs : RoutedEventArgs
{
    /// <summary>Creates the args.</summary>
    public ThumbDragEventArgs(RoutedEvent<ThumbDragEventArgs> routedEvent, UIElement source, int horizontalChange, int verticalChange)
        : base(routedEvent, source)
    {
        HorizontalChange = horizontalChange;
        VerticalChange = verticalChange;
    }

    /// <summary>The cumulative horizontal change in cells since the grab (positive = right).</summary>
    public int HorizontalChange { get; }

    /// <summary>The cumulative vertical change in cells since the grab (positive = down).</summary>
    public int VerticalChange { get; }
}
