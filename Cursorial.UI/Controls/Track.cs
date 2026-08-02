using Cursorial.Drawing.Media;
using Cursorial.Input;
using Cursorial.Output;
using Cursorial.Rendering;

using Cursorial.UI.Input;
using Cursorial.UI.Themes;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.Controls;

/// <summary>
/// Defines the available display modes for a <see cref="ScrollBar"/>'s <see cref="Track">track</see>.
/// </summary>
public enum TrackDisplayMode
{
    /// <summary>
    /// The thumb moves along a visible line that spans the length of the track.
    /// </summary>
    Line,

    /// <summary>
    /// Any parts of the track not occupied by the thumb are filled with the thumb glyph, but at
    /// reduced opacity. The thumb appears to be moving within a bounded area.
    /// </summary>
    Fill,
    
    /// <summary>
    /// Only the thumb is drawn; it appears to float alone in the track region. Similar to the
    /// visuals on modern operating systems and mobile devices.
    /// </summary>
    Hidden
}

/// <summary>
/// The rail + draggable thumb of a <see cref="ScrollBar"/> (design doc §12.7) — an internal primitive
/// (the doc defers <c>Thumb</c>/<c>Track</c> as public types until a second consumer appears). The
/// track maps the owning <see cref="ScrollBar"/>'s <see cref="RangeBase.Value"/> to a thumb cell
/// position along its long axis: a click above/left of the thumb pages back, below/right pages
/// forward, and a drag on the thumb (mouse capture, cell-quantized) reports a continuous value. The
/// track draws a <c>│</c> (or <c>─</c>) rail via the owning bar's <c>BorderPen</c> and a
/// proportional <c>█</c> thumb (minimum 1 cell).
/// </summary>
public sealed class Track : UIElement
{
    /// <summary>
    /// Governs whether the unfilled sections of the track are filled
    /// </summary>
    public static readonly StyledProperty<TrackDisplayMode> TrackDisplayModeProperty =
        UIProperty.Register<Track, TrackDisplayMode>(nameof(TrackDisplayMode),
                                                     defaultValue: TrackDisplayMode.Line);

    public static readonly StyledProperty<IBrush?> TrackFillBrushProperty =
        UIProperty.Register<Track, IBrush?>("TrackFillBrush",
                                            new PropertyMetadata<IBrush?>
                                            {
                                                DefaultResourceKey = ThemeKeys.ScrollBarTrackBrush
                                            });

    public static readonly StyledProperty<IBrush?> ThumbNormalBrushProperty =
        UIProperty.Register<Track, IBrush?>("ThumbNormalBrush",
                                            new PropertyMetadata<IBrush?>
                                            {
                                                DefaultResourceKey = ThemeKeys.ScrollBarThumbNormalBrush
                                            });

    public static readonly StyledProperty<IBrush?> ThumbHoverBrushProperty =
        UIProperty.Register<Track, IBrush?>("ThumbHoverBrush",
                                            new PropertyMetadata<IBrush?>
                                            {
                                                DefaultResourceKey = ThemeKeys.ScrollBarThumbHoverBrush
                                            });

    public static readonly StyledProperty<IBrush?> ThumbDragBrushProperty =
        UIProperty.Register<Track, IBrush?>("ThumbDragBrush",
                                            new PropertyMetadata<IBrush?>
                                            {
                                                DefaultResourceKey = ThemeKeys.ScrollBarThumbDragBrush
                                            });

    private IBrush? _currentBrush;
    private IBrush? _trackFill;
    private bool _dragging;
    private int _dragGrabOffset;  // cells from the thumb's start edge where the drag grabbed

    /// <summary>Parameterless constructor for XAML; the owning <see cref="ScrollBar"/> is resolved from <c>TemplatedParent</c>.</summary>
    public Track() {}

    static Track()
    {
        AffectsRender<Track>(TrackDisplayModeProperty,
                             ThumbNormalBrushProperty,
                             ThumbHoverBrushProperty,
                             ThumbDragBrushProperty);
    }

    /// <summary>The code-first constructor with an explicit owner (used by the code-first ScrollBar template).</summary>
    internal Track(ScrollBar owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public TrackDisplayMode TrackDisplayMode
    {
        get => GetValue(TrackDisplayModeProperty);
        set => SetValue(TrackDisplayModeProperty, value);
    }

    // The owning ScrollBar: the explicit ctor owner (code-first) or the TemplatedParent (XAML template).
    private ScrollBar Owner => field
        ?? TemplatedParent as ScrollBar
        ?? throw new InvalidOperationException("Track requires a ScrollBar owner (the ctor) or a ScrollBar TemplatedParent (XAML).");

    private bool Vertical => Owner.Orientation == Orientation.Vertical;

    /// <summary>The long-axis cell length of the track (rows when vertical, columns when horizontal).</summary>
    private int TrackLength => Vertical ? Bounds.Size.Rows : Bounds.Size.Columns;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) => Size.Empty; // fills whatever the panel gives it

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateBrush();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateBrush();
    }

    internal void UpdateBrush(bool invalidate = true)
    {
        var owner = Owner;
        var oldBrush = _currentBrush;
        var oldTrackFill = _trackFill;

        var hasThumbBrush = GetValueSource(ThumbNormalBrushProperty) is not { Kind: ValueSourceKind.Default };

        var baseNormalBrush = hasThumbBrush
                                  ? GetValue(ThumbNormalBrushProperty) ?? owner.ThumbBrush
                                  : owner.ThumbBrush ?? GetValue(ThumbNormalBrushProperty);

        var normalBrush = baseNormalBrush ?? Brushes.Default;
        var hoverBrush = GetValue(ThumbHoverBrushProperty) ?? normalBrush;
        var dragBrush = GetValue(ThumbDragBrushProperty) ?? hoverBrush;
        var trackFill = GetValue(TrackFillBrushProperty);

        var newBrush = _dragging
                           ? dragBrush
                           : IsPointerOver
                               ? hoverBrush
                               : normalBrush;

        _currentBrush = newBrush;
        _trackFill = trackFill;

        if (invalidate && (oldBrush != newBrush || oldTrackFill != trackFill))
            InvalidateVisual();
    }

    // ───────────────────────────── thumb geometry ─────────────────────────────

    /// <summary>
    /// The thumb's start cell and length along the long axis (matrix C232): length is proportional to
    /// <c>ViewportSize / (Extent = Max − Min + ViewportSize)</c>, clamped to a minimum of 1 cell; the
    /// start maps <see cref="RangeBase.Value"/> across the remaining travel.
    /// </summary>
    internal (int Start, int Length) ThumbGeometry()
    {
        var length = TrackLength;
        if (length <= 0)
            return (0, 0);

        var range = Owner.Maximum - Owner.Minimum;
        if (range == 0)
            return (0, 0);

        var viewport = Math.Max(0, Owner.ViewportSize);
        var minLength = Vertical ? 1 : 2;
        var extent = range + viewport;

        // The proportional thumb length: viewport / extent of the track, at least 1 cell.
        int thumbLength;
        if (extent <= 0 || viewport <= 0)
            thumbLength = length; // nothing to scroll — the thumb fills the rail
        else
            thumbLength = Math.Clamp((int)Math.Round(length * (viewport / extent)), minLength, length);

        var travel = length - thumbLength;
        if (range <= 0 || travel <= 0)
            return (0, thumbLength);

        var fraction = (Owner.Value - Owner.Minimum) / range;
        var start = Math.Clamp((int)Math.Round(travel * fraction), 0, travel);
        return (start, thumbLength);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var bounds = context.Bounds;
        if (bounds.IsEmpty)
            return;

        var pen = Owner.BorderPen ?? Pens.Light;
        var length = TrackLength;

        // The rail: one stroke down/across the track's center line. The thumb's immediate Set cells
        // (below) overwrite the rail glyph at the thumb position.
        var vertical = Vertical;
        var displayMode = TrackDisplayMode;

        if (displayMode is TrackDisplayMode.Line)
        {
            if (vertical)
                context.DrawLine(0, 0, 0, length - 1, pen);
            else
                context.DrawLine(0, 0, length - 1, 0, pen);
        }

        var thumbGrapheme = vertical ? "█" : "▄";

        UpdateBrush(invalidate: false);
        
        // The thumb: a run of full blocks (matrix C232). One cell wide on the short axis. The █ glyph
        // is written as immediate cells (it overwrites the rail). When a ThumbBrush is set the block is
        // tinted by sampling the brush per cell into the glyph's foreground — a background-only
        // FillRectangle here would re-composite the cell against the deferred rail stroke and lose the █.
        // Any IBrush is honored via IBrush.ColorAt over the thumb's own box, so a gradient thumb tints
        // across its length (a solid brush ignores the coordinates and returns one color).
        var (start, thumbLength) = ThumbGeometry();
        var baseBrush = Owner.ThumbBrush ?? GetValue(ThumbNormalBrushProperty) ?? Brushes.Default;
        var brush = _currentBrush ?? Owner.ThumbBrush;
        var thumbBounds = vertical
            ? new Rect(0, start, 1, thumbLength)
            : new Rect(start, 0, thumbLength, 1);

        if (displayMode is TrackDisplayMode.Fill)
        {
            var fillBrush = _trackFill;

            var fill = (fillBrush ?? baseBrush).ColorAt(
                vertical ? 0 : Math.Max(0, thumbBounds.Column + thumbBounds.Columns / 2),
                vertical ? Math.Max(0, thumbBounds.Row + thumbBounds.Rows / 2) : 0,
                thumbBounds);

            if (fillBrush is null)
                fill = fill.WithAlpha(63);

            if (fill is { Kind: ColorKind.Rgb })
            {
                for (int i = 0, n = TrackLength; i < n; i++)
                {
                    var col = vertical ? 0 : i;
                    var row = vertical ? i : 0;
                    var colThumb = vertical ? 0 : start + i;
                    var rowThumb = vertical ? start + i : 0;
                    var isThumb = i >= start && i < start + thumbLength;

                    var tint = isThumb ? brush?.ColorAt(colThumb, rowThumb, thumbBounds) ?? Colors.Default : fill;

                    var style = tint.Kind == ColorKind.Default
                                    ? default
                                    : default(CellStyle) with { Foreground = tint };

                    context.Set(col, row, thumbGrapheme, in style);
                }
                
                return;
            }
        }

        for (var i = 0; i < thumbLength; i++)
        {
            var col = vertical ? 0 : start + i;
            var row = vertical ? start + i : 0;
            var tint = brush?.ColorAt(col, row, thumbBounds) ?? Colors.Default;
            var style = tint.Kind == ColorKind.Default ? default : default(CellStyle) with { Foreground = tint };
            context.Set(col, row, thumbGrapheme, in style);
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
            Owner.PageBy(-1); // click above/left of the thumb pages back (C233)
            return;
        }

        if (coord >= start + thumbLength)
        {
            Owner.PageBy(+1); // click below/right pages forward
            return;
        }

        // On the thumb: begin a drag (capture, cell-quantized).
        _dragging = CaptureMouse();
        _dragGrabOffset = coord - start;
        
        UpdateBrush();

        Owner.OnDragStart();
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
        var range = Owner.Maximum - Owner.Minimum;
        var value = Owner.Minimum + range * thumbStart / travel;
        Owner.OnThumbDrag(value);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left || !_dragging)
            return;

        e.Handled = true;
        // Clear _dragging BEFORE releasing capture: ReleaseMouseCapture synchronously fires OnLostMouseCapture, whose
        // `if (_dragging)` guard would otherwise call OnDragEnd a second time (a double EndScroll). (Mirror of Thumb.)
        _dragging = false;
        UpdateBrush();
        ReleaseMouseCapture();
        Owner.OnDragEnd();
    }

    /// <inheritdoc/>
    protected override void OnLostMouseCapture(RoutedEventArgs e)
    {
        base.OnLostMouseCapture(e);

        if (_dragging)
        {
            _dragging = false;
            UpdateBrush();
            Owner.OnDragEnd();
        }
    }
}
