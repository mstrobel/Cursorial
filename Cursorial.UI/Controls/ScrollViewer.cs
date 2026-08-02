using Cursorial.Input;
using Cursorial.Rendering;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// A scrollable content host (design doc §12.4/§12.7, CD28; inversion 5 — lands at P5 ahead of
/// ListBox): a <see cref="ContentControl"/> whose template wraps S1's banded
/// <see cref="ScrollContentPresenter"/> (<c>PART_ScrollContentPresenter</c>, required) and two
/// optional <see cref="ScrollBar"/>s (<c>PART_VerticalScrollBar</c>/<c>PART_HorizontalScrollBar</c>).
/// <see cref="HorizontalOffset"/>/<see cref="VerticalOffset"/> are <c>DirectProperty</c>
/// two-way mirrors of the SCP's <b>styled</b> offsets (which are <c>AffectsComposite</c> and
/// storyboard-animatable — smooth scroll in v1; the DirectProperty just reflects them). The mouse
/// wheel scrolls <c>WheelDeltaY / 120 × LinesPerNotch</c> rows (Shift / <c>WheelDeltaX</c>
/// horizontal); an unconsumed wheel bubbles to an outer scroller.
/// </summary>
[TemplatePart(PartPresenter, typeof(ScrollContentPresenter), IsRequired = true)]
[TemplatePart(PartVerticalBar, typeof(ScrollBar))]
[TemplatePart(PartHorizontalBar, typeof(ScrollBar))]
public class ScrollViewer : ContentControl
{
    private const string PartPresenter = "PART_ScrollContentPresenter";
    private const string PartVerticalBar = "PART_VerticalScrollBar";
    private const string PartHorizontalBar = "PART_HorizontalScrollBar";

    private ScrollContentPresenter? _presenter;
    private ContentPresenter? _contentHost; // hosts the ScrollViewer's Content inside the SCP
    private ScrollBar? _verticalBar;
    private ScrollBar? _horizontalBar;

    /// <summary>Wires the focus-follows-scroll behavior (a focused descendant is brought into view).</summary>
    public ScrollViewer()
    {
        // handledEventsToo: a control may mark GotFocus handled, but the focused element should still
        // scroll into view. The handler is on `this`, so it persists across template swaps (no leak —
        // same lifetime as the ScrollViewer).
        AddHandler(GotFocusEvent, OnDescendantGotFocus, handledEventsToo: true);
    }

    private IDisposable? _offsetRowObserver;
    private IDisposable? _offsetColumnObserver;
    private int _horizontalOffset;
    private int _verticalOffset;
    private Size _extent;
    private Size _viewport;

    /// <summary>The vertical scrollbar policy (default <see cref="ScrollBarVisibility.Auto"/>). <c>AffectsMeasure</c>.</summary>
    public static readonly StyledProperty<ScrollBarVisibility> VerticalScrollBarVisibilityProperty =
        UIProperty.RegisterAttached<ScrollViewer, UIElement, ScrollBarVisibility>(
            nameof(VerticalScrollBarVisibility),
            defaultValue: ScrollBarVisibility.Auto,
            changed: OnVisibilityChanged);

    /// <summary>
    /// The horizontal scrollbar policy (default <see cref="ScrollBarVisibility.Auto"/>). <c>AffectsMeasure</c>.
    /// <para>
    /// <b>v1 limitation:</b> only the vertical axis is banded (doc §5.7), so horizontal
    /// <see cref="ScrollBarVisibility.Auto"/> — which means "scroll when content overflows, hide the bar
    /// otherwise" — cannot be honored and degrades to <see cref="ScrollBarVisibility.Disabled"/> (a DEBUG
    /// <see cref="ControlDiagnosticKind.HorizontalAutoUnsupported"/> diagnostic is emitted). To allow
    /// horizontal scrolling by wheel/keys today, set <see cref="ScrollBarVisibility.Hidden"/> (scrolls, no
    /// bar) or <see cref="ScrollBarVisibility.Visible"/>. When the horizontal axis is banded (v2), <c>Auto</c>
    /// will gain its overflow semantics.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<ScrollBarVisibility> HorizontalScrollBarVisibilityProperty =
        UIProperty.RegisterAttached<ScrollViewer, UIElement, ScrollBarVisibility>(
            nameof(HorizontalScrollBarVisibility),
            defaultValue: ScrollBarVisibility.Auto,
            changed: OnVisibilityChanged);

    /// <summary>The horizontal scroll offset in cells — a two-way mirror of the SCP's styled <c>ScrollOffsetColumn</c> (CD28).</summary>
    public static readonly DirectProperty<ScrollViewer, int> HorizontalOffsetProperty =
        UIProperty.RegisterDirect<ScrollViewer, int>(nameof(HorizontalOffset), static s => s._horizontalOffset, static (s, v) => s.SetHorizontalOffset(v));

    /// <summary>The vertical scroll offset in cells — a two-way mirror of the SCP's styled <c>ScrollOffsetRow</c> (CD28).</summary>
    public static readonly DirectProperty<ScrollViewer, int> VerticalOffsetProperty =
        UIProperty.RegisterDirect<ScrollViewer, int>(nameof(VerticalOffset), static s => s._verticalOffset, static (s, v) => s.SetVerticalOffset(v));

    /// <summary>The scrollable content size (read-only mirror of the SCP's <c>Extent</c>).</summary>
    public static readonly DirectProperty<ScrollViewer, Size> ExtentProperty =
        UIProperty.RegisterDirect<ScrollViewer, Size>(nameof(Extent), static s => s._extent);

    /// <summary>The visible content size (read-only mirror of the SCP's <c>Viewport</c>).</summary>
    public static readonly DirectProperty<ScrollViewer, Size> ViewportProperty =
        UIProperty.RegisterDirect<ScrollViewer, Size>(nameof(Viewport), static s => s._viewport);

    /// <summary>
    /// The bubbling event raised whenever the scroll geometry moves — an offset, extent, or viewport
    /// change (WPF/Avalonia <c>ScrollViewer.ScrollChanged</c> parity; <see cref="ScrollChangedEventArgs"/>).
    /// Bubbles to mirror <see cref="ScrollBar.ScrollEvent"/>.
    /// </summary>
    public static readonly RoutedEvent<ScrollChangedEventArgs> ScrollChangedEvent =
        RoutedEvent<ScrollChangedEventArgs>.Register(nameof(ScrollChanged), RoutingStrategy.Bubble, typeof(ScrollViewer));

    /// <summary>
    /// Helper for setting VerticalScrollBarVisibility property.
    /// </summary>
    public static void SetVerticalScrollBarVisibility(UIElement element, ScrollBarVisibility verticalScrollBarVisibility)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(VerticalScrollBarVisibilityProperty, verticalScrollBarVisibility);
    }

    /// <summary>
    /// Helper for reading VerticalScrollBarVisibility property.
    /// </summary>
    public static ScrollBarVisibility GetVerticalScrollBarVisibility(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(VerticalScrollBarVisibilityProperty);
    }

    /// <summary>
    /// Helper for setting HorizontalScrollBarVisibility property.
    /// </summary>
    public static void SetHorizontalScrollBarVisibility(UIElement element, ScrollBarVisibility horizontalScrollBarVisibility)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HorizontalScrollBarVisibilityProperty, horizontalScrollBarVisibility);
    }

    /// <summary>
    /// Helper for reading HorizontalScrollBarVisibility property.
    /// </summary>
    public static ScrollBarVisibility GetHorizontalScrollBarVisibility(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(HorizontalScrollBarVisibilityProperty);
    }

    /// <inheritdoc cref="VerticalScrollBarVisibilityProperty"/>
    public ScrollBarVisibility VerticalScrollBarVisibility
    {
        get => GetValue(VerticalScrollBarVisibilityProperty);
        set => SetValue(VerticalScrollBarVisibilityProperty, value);
    }

    /// <inheritdoc cref="HorizontalScrollBarVisibilityProperty"/>
    public ScrollBarVisibility HorizontalScrollBarVisibility
    {
        get => GetValue(HorizontalScrollBarVisibilityProperty);
        set => SetValue(HorizontalScrollBarVisibilityProperty, value);
    }

    /// <inheritdoc cref="HorizontalOffsetProperty"/>
    public int HorizontalOffset
    {
        get => _horizontalOffset;
        set => SetHorizontalOffset(value);
    }

    /// <inheritdoc cref="VerticalOffsetProperty"/>
    public int VerticalOffset
    {
        get => _verticalOffset;
        set => SetVerticalOffset(value);
    }

    /// <inheritdoc cref="ExtentProperty"/>
    public Size Extent => _extent;

    /// <inheritdoc cref="ViewportProperty"/>
    public Size Viewport => _viewport;

    internal Size? ResolvedViewport => _viewport is var vp && LayoutMath.IsBoundedNonEmpty(vp) ? vp : null; 

    /// <summary>CLR sugar over <see cref="ScrollChangedEvent"/>.</summary>
    public event EventHandler<ScrollChangedEventArgs>? ScrollChanged { add => AddHandler(ScrollChangedEvent, value!); remove => RemoveHandler(ScrollChangedEvent, value!); }

    /// <summary>The wrapped scroll-content presenter (the S1-owned banded SCP); null before first template expansion.</summary>
    protected internal ScrollContentPresenter? Presenter => _presenter;

    // ───────────────────────────── template wiring (CD17 / C235) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _presenter = GetTemplatePart<ScrollContentPresenter>(PartPresenter);
        _verticalBar = GetTemplatePart<ScrollBar>(PartVerticalBar);
        _horizontalBar = GetTemplatePart<ScrollBar>(PartHorizontalBar);

        if (_presenter is {} presenter)
        {
            // The SCP hosts the ScrollViewer's content. A UIElement content already logical-parents to
            // this ScrollViewer (ContentControl, chain ③), so the SCP hosts it visual-only (its Content
            // setter detects the existing logical parent). Non-element content rides a ContentPresenter
            // that runs the §12.3 realization chain (the presenter freshly owns its built child).
            _contentHost = Content is not UIElement ? new ContentPresenter { Content = Content } : null;
            presenter.Content = Content as UIElement ?? _contentHost;
            UpdatePresenterScrollAxes();

            // The DirectProperty mirrors track the styled SCP offsets (CD28): a styled-side change
            // (incl. an animated write, the re-anchor, and the end-of-arrange coercion) reflects here —
            // this is the zero-re-raster composite-slide reflection (invariant 3, C240).
            _offsetRowObserver = presenter.AddObserver(ScrollContentPresenter.ScrollOffsetRowProperty, new OffsetRowObserver(this));
            _offsetColumnObserver = presenter.AddObserver(ScrollContentPresenter.ScrollOffsetColumnProperty, new OffsetColumnObserver(this));

            SyncFromPresenter();
        }

        // Bar wiring is code-behind (TemplateBinding is one-way; CD17/C235): a bar's Scroll moves the
        // offset; the offset mirror moves the bar's Value back.
        if (_verticalBar is {} vBar)
        {
            vBar.Orientation = Orientation.Vertical;
            vBar.Scroll += OnVerticalScroll;
        }

        if (_horizontalBar is {} hBar)
        {
            hBar.Orientation = Orientation.Horizontal;
            hBar.Scroll += OnHorizontalScroll;
        }
    }

    static ScrollViewer()
    {
        // Re-point the SCP when the ScrollViewer's Content changes after template application: an
        // element content hosts directly (visual-only), a non-element content rides the inner
        // ContentPresenter (see OnApplyTemplate). The base ContentControl handler runs first
        // (metadata Changed callbacks chain base-first), so the logical adoption is already applied.
        ContentProperty.OverrideMetadata<ScrollViewer>(
            new PropertyMetadata<object?>
            {
                Changed = static (sender, _, newValue) =>
                          {
                              if (sender is ScrollViewer { _presenter: {} presenter } viewer)
                              {
                                  if (newValue is UIElement element)
                                  {
                                      viewer._contentHost = null;
                                      presenter.Content = element;
                                  }
                                  else
                                  {
                                      viewer._contentHost ??= new ContentPresenter();
                                      viewer._contentHost.Content = newValue;
                                      presenter.Content = viewer._contentHost;
                                  }
                              }
                          }
            });
    }

    /// <inheritdoc/>
    protected override void OnTemplateDetaching(TemplateInstance old)
    {
        // Release the content's visual link from the old SCP before the new template's SCP adopts it
        // (the content is the ScrollViewer's stable logical child across templates; each template makes
        // a fresh SCP). Clearing synchronously here avoids a stale visual-parent on the next adopt.
        if (_presenter is {} presenter)
            presenter.Content = null;

        if (_contentHost is {} host)
            host.Content = null;

        _contentHost = null;
        _offsetRowObserver?.Dispose();
        _offsetColumnObserver?.Dispose();
        _offsetRowObserver = _offsetColumnObserver = null;

        if (_verticalBar is {} vBar)
            vBar.Scroll -= OnVerticalScroll;

        if (_horizontalBar is {} hBar)
            hBar.Scroll -= OnHorizontalScroll;

        _presenter = null;
        _verticalBar = null;
        _horizontalBar = null;
        base.OnTemplateDetaching(old);
    }

    // ───────────────────────────── offset mirrors (CD28) ─────────────────────────────

    private void SetVerticalOffset(int value)
    {
        if (_presenter is {} presenter)
            presenter.ScrollOffsetRow = value; // styled offset coerces; the observer mirrors it back
        else
            SetAndRaise(VerticalOffsetProperty, ref _verticalOffset, Math.Max(0, value));
    }

    private void SetHorizontalOffset(int value)
    {
        if (_presenter is {} presenter)
            presenter.ScrollOffsetColumn = value;
        else
            SetAndRaise(HorizontalOffsetProperty, ref _horizontalOffset, Math.Max(0, value));
    }

    private void SyncFromPresenter()
    {
        if (_presenter is not {} presenter)
            return;

        // Hold the prior offsets for the ScrollChanged deltas before SetAndRaise overwrites the fields.
        var priorVerticalOffset = _verticalOffset;
        var priorHorizontalOffset = _horizontalOffset;

        var verticalMoved = SetAndRaise(VerticalOffsetProperty, ref _verticalOffset, presenter.ScrollOffsetRow);
        var horizontalMoved = SetAndRaise(HorizontalOffsetProperty, ref _horizontalOffset, presenter.ScrollOffsetColumn);
        var extentMoved = SetAndRaise(ExtentProperty, ref _extent, presenter.Extent);
        var viewportMoved = SetAndRaise(ViewportProperty, ref _viewport, presenter.Viewport);

        UpdateBars();

        // ScrollChanged fires once per sync when any of offset/extent/viewport actually moved (WPF/Avalonia
        // parity): the offsets/sizes are the settled values, the changes are deltas from the pre-sync offsets.
        if (verticalMoved || horizontalMoved || extentMoved || viewportMoved)
        {
            RaiseEvent(new ScrollChangedEventArgs(ScrollChangedEvent, this,
                                                  _horizontalOffset, _verticalOffset, _extent, _viewport,
                                                  _horizontalOffset - priorHorizontalOffset,
                                                  _verticalOffset - priorVerticalOffset));
        }
    }

    /// <summary>Maps the visibility policies onto the SCP's scroll-axis enables (CD28/C230).</summary>
    private void UpdatePresenterScrollAxes()
    {
        if (_presenter is not {} presenter)
            return;

        presenter.CanScrollVertically = CanScrollVerticalAxis(VerticalScrollBarVisibility);
        presenter.CanScrollHorizontally = CanScrollHorizontalAxis(HorizontalScrollBarVisibility);

        // // v1 bands only the vertical axis (doc §5.7), so horizontal Auto ("show bar on overflow")
        // // cannot be honored and degrades to Disabled — surface that, since it silently swallows intent.
        // if (HorizontalScrollBarVisibility == ScrollBarVisibility.Auto)
        //     ControlDiagnostics.HorizontalAutoUnsupported(this);
    }

    /// <summary>
    /// The vertical-axis scroll-enable: every policy except <see cref="ScrollBarVisibility.Disabled"/>
    /// lets the axis scroll (<c>Hidden</c>/<c>Auto</c>/<c>Visible</c> all scroll by wheel/keys; the bar
    /// visibility is a separate concern, CD28).
    /// </summary>
    internal static bool CanScrollVerticalAxis(ScrollBarVisibility visibility)
        => visibility is not ScrollBarVisibility.Disabled;

    /// <summary>
    /// The horizontal-axis scroll-enable: <c>Visible</c> or <c>Hidden</c> scroll by wheel/keys;
    /// <c>Disabled</c> never scrolls, and v1 treats <c>Auto</c> as <c>Disabled</c> because the
    /// horizontal axis is unbanded (doc §5.7) — see <see cref="HorizontalScrollBarVisibility"/>.
    /// </summary>
    internal static bool CanScrollHorizontalAxis(ScrollBarVisibility visibility)
        => visibility is not ScrollBarVisibility.Disabled;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        // The SCP published its Extent/Viewport during this arrange pass — mirror them now (the
        // observers only fire on offset changes, the composite-slide path).
        SyncFromPresenter();
        return size;
    }

    private void UpdateBars()
    {
        if (_verticalBar is {} vBar)
        {
            vBar.Minimum = 0;
            vBar.Maximum = Math.Max(0, _extent.Rows - _viewport.Rows);
            vBar.ViewportSize = _viewport.Rows;
            vBar.SetValueSilently(_verticalOffset);
        }

        if (_horizontalBar is {} hBar)
        {
            hBar.Minimum = 0;
            hBar.Maximum = Math.Max(0, _extent.Columns - _viewport.Columns);
            hBar.ViewportSize = _viewport.Columns;
            hBar.SetValueSilently(_horizontalOffset);
        }

        UpdateBarVisibility();
    }

    /// <summary>
    /// Resolves each bar's element <see cref="UIElement.Visibility"/> from its policy + current overflow
    /// (CD28). The policy gated only the axis <em>enable</em> before; the bar's own visibility was never
    /// driven, so a <c>Visible</c>/<c>Hidden</c>/<c>Disabled</c> request had no effect on the rendered bar.
    /// <c>Visible</c> always shows; <c>Auto</c> shows only when the axis overflows (<c>Maximum &gt; 0</c>);
    /// <c>Hidden</c>/<c>Disabled</c> collapse the bar (<c>Hidden</c> still scrolls by wheel/keys — that
    /// enable is handled in <see cref="UpdatePresenterScrollAxes"/>). The bar is <c>Collapsed</c>, not
    /// <c>Hidden</c>, so an absent bar reserves no DockPanel track. The horizontal axis is unbanded in v1,
    /// so its <c>Auto</c> degrades to <c>Disabled</c> (no bar) — see <see cref="HorizontalScrollBarVisibility"/>.
    /// </summary>
    private void UpdateBarVisibility()
    {
        if (_verticalBar is {} vBar)
            vBar.Visibility = ResolveBarVisibility(VerticalScrollBarVisibility, vBar.Maximum > 0);

        if (_horizontalBar is {} hBar)
        {
            // v1 bands only the vertical axis (doc §5.7): horizontal Auto degrades to Disabled, so its bar
            // never shows (it would be a non-functional bar — CanScrollHorizontalAxis is false for Auto).
            var policy = HorizontalScrollBarVisibility/* == ScrollBarVisibility.Auto
                             ? ScrollBarVisibility.Disabled
                             : HorizontalScrollBarVisibility*/;

            hBar.Visibility = ResolveBarVisibility(policy, hBar.Maximum > 0);
        }
    }

    private static Visibility ResolveBarVisibility(ScrollBarVisibility policy, bool overflowing)
        => policy switch
           {
               ScrollBarVisibility.Visible => Visibility.Visible,
               ScrollBarVisibility.Auto    => overflowing ? Visibility.Visible : Visibility.Collapsed,
               _                           => Visibility.Collapsed // Hidden / Disabled
           };

    private void OnVerticalScroll(object? sender, ScrollEventArgs e)
        => SetVerticalOffset((int) Math.Round(e.NewValue));

    private void OnHorizontalScroll(object? sender, ScrollEventArgs e)
        => SetHorizontalOffset((int) Math.Round(e.NewValue));

    // ───────────────────────────── ScrollBy / EnsureVisible (C225/C226) ─────────────────────────────

    /// <summary>Scrolls by the cell deltas, coercing into range (the keyboard / wheel scroll primitive).</summary>
    public void ScrollBy(int columns, int rows)
    {
        if (rows != 0)
            SetVerticalOffset(_verticalOffset + rows);

        if (columns != 0)
            SetHorizontalOffset(_horizontalOffset + columns);
    }

    /// <summary>
    /// Scrolls minimally to bring an element-local content <paramref name="rect"/> into the viewport
    /// (C226 — ListBox/TextBox call this at P9). The rect is in content coordinates.
    /// </summary>
    public void EnsureVisible(Rect rect)
    {
        var newV = ComputeMinimalScrollOffset(_verticalOffset, _viewport.Rows, rect.Row, rect.RowEnd);
        if (newV != _verticalOffset)
            SetVerticalOffset(newV);

        var newH = ComputeMinimalScrollOffset(_horizontalOffset, _viewport.Columns, rect.Column, rect.ColumnEnd);
        if (newH != _horizontalOffset)
            SetHorizontalOffset(newH);
    }

    /// <summary>
    /// The least scroll that brings <c>[rectStart, rectEnd)</c> into a <paramref name="viewportExtent"/>-sized
    /// viewport currently at <paramref name="currentOffset"/> (WPF's <c>ComputeScrollOffsetWithMinimalScroll</c>,
    /// one axis). The load-bearing rule is the <em>larger-than-viewport</em> case: when the target exceeds the
    /// viewport, align its <b>leading</b> edge so the start stays visible — aligning the trailing edge instead
    /// would push the start out. That is exactly the expanded-<see cref="TreeViewItem"/> case: the focused
    /// node's bounds span its header <em>and</em> its whole subtree, so scrolling to the subtree's bottom would
    /// shove the header off the top; the leading-edge alignment keeps the header in view.
    /// </summary>
    private static int ComputeMinimalScrollOffset(int currentOffset, int viewportExtent, int rectStart, int rectEnd)
    {
        var extent = Math.Max(0, viewportExtent);
        var viewStart = currentOffset;
        var viewEnd = currentOffset + extent;

        var before = rectStart < viewStart && rectEnd < viewEnd; // starts before the viewport, doesn't cover its end
        var after = rectEnd > viewEnd && rectStart > viewStart;   // ends after the viewport, starts inside it
        var larger = rectEnd - rectStart > extent;

        // Align the leading edge: the rect fits and starts before the viewport, OR it is larger and trails past
        // it (WPF cases 1 & 4 — case 4 is the expanded-node fix).
        if (before && !larger || after && larger)
            return rectStart;

        // Align the trailing edge: the rect fits and trails past the viewport, OR it is larger and starts before
        // it (WPF cases 2 & 3).
        if (before || after)
            return rectEnd - extent;

        // Already visible enough (fully inside, or an oversized rect already covering the viewport) — no scroll.
        return currentOffset;
    }

    /// <summary>
    /// Brings a newly keyboard-focused descendant into view (the focus-follows-scroll behavior — WPF's
    /// BringIntoView-on-focus): a minimal scroll so arrow / Tab navigation keeps the focused element
    /// visible. Pairs with the <see cref="Control.HandlesScrolling"/> gate in <see cref="OnKeyDown"/> —
    /// the selector moves selection + focus, this follows the focus on screen. A focus on something
    /// outside the scrolled content (resolved by the SCP's content-coordinate translation) is ignored.
    /// </summary>
    private void OnDescendantGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.OriginalSource is { } focused && _presenter is { } presenter && presenter.TryGetContentRect(focused, out var rect))
            EnsureVisible(rect);
    }

    // ───────────────────────────── wheel + keyboard (CD28 / §12.7) ─────────────────────────────

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (e.Handled)
            return;

        var notchLines = e.LinesPerNotch; // default 3 (CD28)

        // Shift+wheel or a horizontal wheel scrolls the horizontal axis.
        var horizontal = e.WheelDeltaX != 0 || (e.Modifiers & KeyModifiers.Shift) != 0;

        if (horizontal)
        {
            var deltaX = e.WheelDeltaX != 0 ? e.WheelDeltaX : -e.WheelDeltaY;
            var lines = deltaX / 120 * notchLines;

            if (TryScrollHorizontally(lines))
                e.Handled = true;

            return;
        }

        var rows = e.WheelDeltaY / 120 * notchLines;

        if (TryScrollVertically(-rows))
            e.Handled = true;
        // An unconsumed wheel (already at the extreme) bubbles to an outer ScrollViewer (C224).
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled)
            return;

        // WPF parity (Control.HandlesScrolling): a templated parent that owns keyboard scroll navigation —
        // a Selector moving its selection on the arrow / Home / End / Page keys — handles those keys itself.
        // The inner ScrollViewer sits BELOW that control in the bubble route, so without this gate it would
        // consume the keys first and scroll the extent out from under the selection (the "scrolls to the
        // bottom before the selection moves" bug). Leave the event unhandled so it bubbles up to the control.
        if (TemplatedParent is Control { HandlesScrolling: true })
            return;

        var maxRow = Math.Max(0, _extent.Rows - _viewport.Rows);

        switch (e.Key)
        {
            case Key.UpArrow when e.Modifiers == KeyModifiers.None:
                if (TryScrollVertically(LineDelta(-1, vertical: true))) e.Handled = true;
                break;

            case Key.DownArrow when e.Modifiers == KeyModifiers.None:
                if (TryScrollVertically(LineDelta(+1, vertical: true))) e.Handled = true;
                break;

            case Key.PageUp when e.Modifiers == KeyModifiers.None:
                if (TryScrollVertically(PageDelta(-1, vertical: true))) e.Handled = true;
                break;

            case Key.PageDown when e.Modifiers == KeyModifiers.None:
                if (TryScrollVertically(PageDelta(+1, vertical: true))) e.Handled = true;
                break;

            case Key.Home when (e.Modifiers & KeyModifiers.Control) != 0:
                if (_verticalOffset != 0)
                {
                    SetVerticalOffset(0);
                    e.Handled = true;
                }

                break;

            case Key.End when (e.Modifiers & KeyModifiers.Control) != 0:
                if (_verticalOffset != maxRow)
                {
                    SetVerticalOffset(maxRow);
                    e.Handled = true;
                }

                break;

            case Key.LeftArrow when e.Modifiers == KeyModifiers.None:
                if (TryScrollHorizontally(LineDelta(-1, vertical: false))) e.Handled = true;
                break;

            case Key.RightArrow when e.Modifiers == KeyModifiers.None:
                if (TryScrollHorizontally(LineDelta(+1, vertical: false))) e.Handled = true;
                break;
        }
    }

    // Content-assisted line/page step (§12.6): when the content opts in as a logical-scroll IScrollContentHost it
    // supplies the cell step, so a line/page scroll snaps to its logical units (whole items / tiles); otherwise the
    // legacy fixed step (one cell / one viewport). The offset stays SCP-owned cells either way.
    private IScrollContentHost? StepHost
        => _presenter?.ScrollHost is { IsScrollClient: true, IsLogicalScroll: true } host ? host : null;

    private int LineDelta(int sign, bool vertical)
    {
        var host = StepHost;
        if (host is null)
            return sign;
        var offset = vertical ? _verticalOffset : _horizontalOffset;
        return sign * Math.Max(1, host.LineStep(offset, sign, vertical));
    }

    private int PageDelta(int sign, bool vertical)
    {
        var host = StepHost;
        var offset = vertical ? _verticalOffset : _horizontalOffset;
        return host is null
            ? sign * Math.Max(1, vertical ? _viewport.Rows : _viewport.Columns)
            : sign * Math.Max(1, host.PageStep(offset, sign, vertical));
    }

    private bool TryScrollVertically(int rows)
    {
        if (rows == 0 || _presenter is not { CanScrollVertically: true })
            return false;

        var max = Math.Max(0, _extent.Rows - _viewport.Rows);
        var target = Math.Clamp(_verticalOffset + rows, 0, max);

        if (target == _verticalOffset)
            return false;

        SetVerticalOffset(target);
        return true;
    }

    private bool TryScrollHorizontally(int columns)
    {
        if (columns == 0 || _presenter is not { CanScrollHorizontally: true })
            return false;

        var max = Math.Max(0, _extent.Columns - _viewport.Columns);
        var target = Math.Clamp(_horizontalOffset + columns, 0, max);

        if (target == _horizontalOffset)
            return false;

        SetHorizontalOffset(target);
        return true;
    }

    private static void OnVisibilityChanged(UIObject sender, ScrollBarVisibility oldValue, ScrollBarVisibility newValue)
    {
        if (sender is ScrollViewer viewer)
        {
            viewer.UpdatePresenterScrollAxes(); // single source of truth for the axis enables + the Auto diagnostic
            viewer.UpdateBarVisibility();       // and the bar's own Visibility (CD28 — the reported gap)
            viewer.InvalidateMeasure();         // a collapsed/shown bar reflows the DockPanel track
        }
    }

    // The styled-offset / extent / viewport observers feeding the DirectProperty mirrors (CD28).
    private sealed class OffsetRowObserver(ScrollViewer viewer) : IValueObserver<int>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, int oldValue, int newValue, BindingPriority priority)
            => viewer.SyncFromPresenter();
    }

    private sealed class OffsetColumnObserver(ScrollViewer viewer) : IValueObserver<int>
    {
        public void OnPropertyChanged(UIObject source, UIProperty property, int oldValue, int newValue, BindingPriority priority)
            => viewer.SyncFromPresenter();
    }

    // Extent/Viewport are mirrored by SyncFromPresenter (the SCP publishes them during arrange, then
    // ArrangeOverride pulls; the offset observers cover the in-band composite-slide path) — no eager
    // push observers are needed for those two (CD28).
}