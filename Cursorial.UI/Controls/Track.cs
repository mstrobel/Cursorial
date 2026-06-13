using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;

using Cursorial.UI.Input;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.Controls;

/// <summary>
/// The rail + draggable thumb of a <see cref="ScrollBar"/> (design doc §12.7) — an internal primitive
/// (the doc defers <c>Thumb</c>/<c>Track</c> as public types until a second consumer appears). The
/// track maps the owning <see cref="ScrollBar"/>'s <see cref="ScrollBar.Value"/> to a thumb cell
/// position along its long axis: a click above/left of the thumb pages back, below/right pages
/// forward, and a drag on the thumb (mouse capture, cell-quantized) reports a continuous value. The
/// track draws a <c>│</c> (or <c>─</c>) rail via the owning bar's <c>BorderPen</c> and a
/// proportional <c>█</c> thumb (minimum 1 cell).
/// </summary>
public sealed class Track : UIElement
{
    private readonly ScrollBar _owner;
    private bool _dragging;
    private int _dragGrabOffset;  // cells from the thumb's start edge where the drag grabbed

    internal Track(ScrollBar owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    private bool Vertical => _owner.Orientation == Orientation.Vertical;

    /// <summary>The long-axis cell length of the track (rows when vertical, columns when horizontal).</summary>
    private int TrackLength => Vertical ? Bounds.Size.Rows : Bounds.Size.Columns;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => Size.Empty; // fills whatever the panel gives it

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    // ───────────────────────────── thumb geometry ─────────────────────────────

    /// <summary>
    /// The thumb's start cell and length along the long axis (matrix C232): length is proportional to
    /// <c>ViewportSize / (Extent = Max − Min + ViewportSize)</c>, clamped to a minimum of 1 cell; the
    /// start maps <see cref="ScrollBar.Value"/> across the remaining travel.
    /// </summary>
    internal (int Start, int Length) ThumbGeometry()
    {
        var length = TrackLength;
        if (length <= 0)
            return (0, 0);

        var range = _owner.Maximum - _owner.Minimum;
        var viewport = Math.Max(0, _owner.ViewportSize);
        var extent = range + viewport;

        // The proportional thumb length: viewport / extent of the track, at least 1 cell.
        int thumbLength;
        if (extent <= 0 || viewport <= 0)
            thumbLength = length; // nothing to scroll — the thumb fills the rail
        else
            thumbLength = Math.Clamp((int)Math.Round(length * (viewport / extent)), 1, length);

        var travel = length - thumbLength;
        if (range <= 0 || travel <= 0)
            return (0, thumbLength);

        var fraction = (_owner.Value - _owner.Minimum) / range;
        var start = Math.Clamp((int)Math.Round(travel * fraction), 0, travel);
        return (start, thumbLength);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        var pen = _owner.BorderPen ?? Pens.Light;
        var length = TrackLength;

        // The rail: one stroke down/across the track's center line. The thumb's immediate Set cells
        // (below) overwrite the rail glyph at the thumb position.
        if (Vertical)
            context.DrawLine(0, 0, 0, length - 1, pen);
        else
            context.DrawLine(0, 0, length - 1, 0, pen);

        // The thumb: a run of full blocks (matrix C232). One cell wide on the short axis. The █ glyph
        // is written as immediate cells (it overwrites the rail). When a ThumbBrush is set the block is
        // tinted by sampling the brush per cell into the glyph's foreground — a background-only
        // FillRectangle here would re-composite the cell against the deferred rail stroke and lose the █.
        // Any IBrush is honored via IBrush.ColorAt over the thumb's own box, so a gradient thumb tints
        // across its length (a solid brush ignores the coordinates and returns one color).
        var (start, thumbLength) = ThumbGeometry();
        var brush = _owner.ThumbBrush;
        var thumbBounds = Vertical
            ? new Rect(0, start, 1, thumbLength)
            : new Rect(start, 0, thumbLength, 1);

        for (var i = 0; i < thumbLength; i++)
        {
            var col = Vertical ? 0 : start + i;
            var row = Vertical ? start + i : 0;
            var tint = brush?.ColorAt(col, row, thumbBounds) ?? Colors.Default;
            var style = tint.Kind == ColorKind.Default ? default : default(CellStyle) with { Foreground = tint };
            context.Set(col, row, "█", in style);
        }
    }

    // ───────────────────────────── mouse: track paging + thumb drag ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        var position = e.GetPosition(this);
        var coord = Vertical ? position.Row : position.Column;
        var (start, thumbLength) = ThumbGeometry();

        e.Handled = true;

        if (coord < start)
        {
            _owner.PageBy(-1); // click above/left of the thumb pages back (C233)
            return;
        }

        if (coord >= start + thumbLength)
        {
            _owner.PageBy(+1); // click below/right pages forward
            return;
        }

        // On the thumb: begin a drag (capture, cell-quantized).
        _dragging = CaptureMouse();
        _dragGrabOffset = coord - start;
        _owner.OnDragStart();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;

        var position = e.GetPosition(this);
        var coord = Vertical ? position.Row : position.Column;
        var (_, thumbLength) = ThumbGeometry();
        var travel = TrackLength - thumbLength;
        if (travel <= 0)
            return;

        var thumbStart = Math.Clamp(coord - _dragGrabOffset, 0, travel);
        var range = _owner.Maximum - _owner.Minimum;
        var value = _owner.Minimum + range * thumbStart / travel;
        _owner.OnThumbDrag(value);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left || !_dragging)
            return;

        e.Handled = true;
        ReleaseMouseCapture();
        _dragging = false;
        _owner.OnDragEnd();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            _owner.OnDragEnd();
        }
    }
}
