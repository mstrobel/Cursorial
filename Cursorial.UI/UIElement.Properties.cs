using Cursorial.Rendering;

namespace Cursorial.UI;

public abstract partial class UIElement
{
    // ───────────────────────────── layout / render styled properties (doc §5.1) ─────────────────────────────

    /// <summary>The explicit width in cells; <see langword="null"/> = Auto (size to content). Binding strength: Min &gt; Max &gt; explicit &gt; content (LD1).</summary>
    public static readonly StyledProperty<int?> WidthProperty =
        UIProperty.Register<UIElement, int?>(nameof(Width));

    /// <summary>The explicit height in cells; <see langword="null"/> = Auto (size to content).</summary>
    public static readonly StyledProperty<int?> HeightProperty =
        UIProperty.Register<UIElement, int?>(nameof(Height));

    /// <summary>The minimum width in cells. Min beats Max and explicit <see cref="Width"/> (LD1).</summary>
    public static readonly StyledProperty<int> MinWidthProperty =
        UIProperty.Register<UIElement, int>(nameof(MinWidth));

    /// <summary>The minimum height in cells.</summary>
    public static readonly StyledProperty<int> MinHeightProperty =
        UIProperty.Register<UIElement, int>(nameof(MinHeight));

    /// <summary>The maximum width in cells (default <see cref="LayoutMath.Unbounded"/>).</summary>
    public static readonly StyledProperty<int> MaxWidthProperty =
        UIProperty.Register<UIElement, int>(nameof(MaxWidth), defaultValue: LayoutMath.Unbounded);

    /// <summary>The maximum height in cells (default <see cref="LayoutMath.Unbounded"/>).</summary>
    public static readonly StyledProperty<int> MaxHeightProperty =
        UIProperty.Register<UIElement, int>(nameof(MaxHeight), defaultValue: LayoutMath.Unbounded);

    /// <summary>
    /// The margin around the element, inside its layout slot. Negative components are unsupported
    /// in v1 and coerce to 0 with a DEBUG diagnostic (doc §5.2 — overlap effects use
    /// <see cref="RenderOffsetColumn"/>/<see cref="RenderOffsetRow"/>).
    /// </summary>
    public static readonly StyledProperty<Margins> MarginProperty =
        UIProperty.Register<UIElement, Margins>(nameof(Margin), coerce: CoerceMargin);

    /// <summary>Horizontal placement within the layout slot (default <see cref="HorizontalAlignment.Stretch"/>).</summary>
    public static readonly StyledProperty<HorizontalAlignment> HorizontalAlignmentProperty =
        UIProperty.Register<UIElement, HorizontalAlignment>(nameof(HorizontalAlignment));

    /// <summary>Vertical placement within the layout slot (default <see cref="VerticalAlignment.Stretch"/>).</summary>
    public static readonly StyledProperty<VerticalAlignment> VerticalAlignmentProperty =
        UIProperty.Register<UIElement, VerticalAlignment>(nameof(VerticalAlignment));

    /// <summary>
    /// The element's visibility. Routing is custom (LD5, doc §5.6): flips into/out of
    /// <see cref="Visibility.Collapsed"/> invalidate measure (self + parent via the ancestor walk);
    /// <see cref="Visibility.Visible"/> ↔ <see cref="Visibility.Hidden"/> flips are render-side only.
    /// </summary>
    public static readonly StyledProperty<Visibility> VisibilityProperty =
        UIProperty.Register<UIElement, Visibility>(nameof(Visibility));

    /// <summary>
    /// Whether this element itself can be hit (default true). Gates the <b>leaf only</b> — children
    /// remain hittable (doc §5.8 "gate the leaf"; deliberate deviation from WPF's subtree semantics).
    /// </summary>
    public static readonly StyledProperty<bool> IsHitTestVisibleProperty =
        UIProperty.Register<UIElement, bool>(nameof(IsHitTestVisible), defaultValue: true);

    /// <summary>
    /// Whether the element is enabled (default true). The effective state is
    /// <see cref="IsEffectivelyEnabled"/> — this value AND <see cref="IsEnabledCore"/> AND the
    /// parent's effective state (doc §5.1).
    /// </summary>
    public static readonly StyledProperty<bool> IsEnabledProperty =
        UIProperty.Register<UIElement, bool>(nameof(IsEnabled), defaultValue: true);

    /// <summary>Sibling paint/composite order within the parent: stable <c>(ZIndex, index)</c> sort. [AffectsRender + z-order recollect at T3]</summary>
    public static readonly StyledProperty<int> ZIndexProperty =
        UIProperty.Register<UIElement, int>(nameof(ZIndex));

    /// <summary>
    /// The element's opacity (default 1.0). <c>[AffectsComposite]</c>: a value &lt; 1 promotes the
    /// element to a render boundary (sticky until detach — T3); fades re-composite a cached raster
    /// and never re-raster (invariant 3).
    /// </summary>
    public static readonly StyledProperty<double> OpacityProperty =
        UIProperty.Register<UIElement, double>(nameof(Opacity), defaultValue: 1.0);

    /// <summary>Whether the element clips its content to its bounds (default false). <c>[AffectsComposite]</c>; true promotes a boundary (T3).</summary>
    public static readonly StyledProperty<bool> ClipToBoundsProperty =
        UIProperty.Register<UIElement, bool>(nameof(ClipToBounds));

    /// <summary>
    /// A composite-time clip rectangle in element-local coordinates — the reveal/wipe animation lane
    /// (S5). <c>[AffectsComposite]</c>; non-null promotes a boundary (doc §5.5 predicate ⑤, T3).
    /// </summary>
    public static readonly StyledProperty<Rect?> CompositeClipProperty =
        UIProperty.Register<UIElement, Rect?>(nameof(CompositeClip));

    /// <summary>
    /// A composite-time column slide, may be negative. <c>[AffectsComposite]</c>; non-zero promotes
    /// a boundary (T3). <b>Animate position via this property</b> (the re-composite path), never via
    /// <see cref="Margin"/>/<c>Canvas.Left</c> (the re-raster path) — invariant 3.
    /// </summary>
    public static readonly StyledProperty<int> RenderOffsetColumnProperty =
        UIProperty.Register<UIElement, int>(nameof(RenderOffsetColumn));

    /// <summary>A composite-time row slide, may be negative. See <see cref="RenderOffsetColumnProperty"/>.</summary>
    public static readonly StyledProperty<int> RenderOffsetRowProperty =
        UIProperty.Register<UIElement, int>(nameof(RenderOffsetRow));

    /// <summary>
    /// The explicit render-boundary cache hint (doc §5.5 predicate ⑦) for expensive-to-raster,
    /// rarely-changing subtrees. Promotion is <b>sticky until detach</b> (T3): setting this back to
    /// <see langword="false"/> after any promotion demotes nothing.
    /// </summary>
    public static readonly StyledProperty<bool> IsRenderBoundaryProperty =
        UIProperty.Register<UIElement, bool>(nameof(IsRenderBoundary));

    /// <summary>The element's desired size including <see cref="Margin"/> (the WPF rule), produced by <see cref="Measure"/>. Read-only direct property.</summary>
    public static readonly DirectProperty<UIElement, Size> DesiredSizeProperty =
        UIProperty.RegisterDirect<UIElement, Size>(nameof(DesiredSize), static e => e._desiredSize);

    /// <summary>The element's parent-relative arranged bounds, produced by <see cref="Arrange"/>. Read-only direct property.</summary>
    public static readonly DirectProperty<UIElement, Rect> BoundsProperty =
        UIProperty.RegisterDirect<UIElement, Rect>(nameof(Bounds), static e => e._bounds);

    static UIElement()
    {
        AffectsMeasure<UIElement>(
            WidthProperty, HeightProperty,
            MinWidthProperty, MinHeightProperty, MaxWidthProperty, MaxHeightProperty,
            MarginProperty);
        AffectsArrange<UIElement>(HorizontalAlignmentProperty, VerticalAlignmentProperty);
        AffectsRender<UIElement>(ZIndexProperty);
        AffectsComposite<UIElement>(
            OpacityProperty, ClipToBoundsProperty, CompositeClipProperty,
            RenderOffsetColumnProperty, RenderOffsetRowProperty, IsRenderBoundaryProperty);
    }

    // ───────────────────────────── CLR wrappers ─────────────────────────────

    /// <inheritdoc cref="WidthProperty"/>
    public int? Width { get => GetValue(WidthProperty); set => SetValue(WidthProperty, value); }

    /// <inheritdoc cref="HeightProperty"/>
    public int? Height { get => GetValue(HeightProperty); set => SetValue(HeightProperty, value); }

    /// <inheritdoc cref="MinWidthProperty"/>
    public int MinWidth { get => GetValue(MinWidthProperty); set => SetValue(MinWidthProperty, value); }

    /// <inheritdoc cref="MinHeightProperty"/>
    public int MinHeight { get => GetValue(MinHeightProperty); set => SetValue(MinHeightProperty, value); }

    /// <inheritdoc cref="MaxWidthProperty"/>
    public int MaxWidth { get => GetValue(MaxWidthProperty); set => SetValue(MaxWidthProperty, value); }

    /// <inheritdoc cref="MaxHeightProperty"/>
    public int MaxHeight { get => GetValue(MaxHeightProperty); set => SetValue(MaxHeightProperty, value); }

    /// <inheritdoc cref="MarginProperty"/>
    public Margins Margin { get => GetValue(MarginProperty); set => SetValue(MarginProperty, value); }

    /// <inheritdoc cref="HorizontalAlignmentProperty"/>
    public HorizontalAlignment HorizontalAlignment { get => GetValue(HorizontalAlignmentProperty); set => SetValue(HorizontalAlignmentProperty, value); }

    /// <inheritdoc cref="VerticalAlignmentProperty"/>
    public VerticalAlignment VerticalAlignment { get => GetValue(VerticalAlignmentProperty); set => SetValue(VerticalAlignmentProperty, value); }

    /// <inheritdoc cref="VisibilityProperty"/>
    public Visibility Visibility { get => GetValue(VisibilityProperty); set => SetValue(VisibilityProperty, value); }

    /// <inheritdoc cref="IsHitTestVisibleProperty"/>
    public bool IsHitTestVisible { get => GetValue(IsHitTestVisibleProperty); set => SetValue(IsHitTestVisibleProperty, value); }

    /// <inheritdoc cref="IsEnabledProperty"/>
    public bool IsEnabled { get => GetValue(IsEnabledProperty); set => SetValue(IsEnabledProperty, value); }

    /// <inheritdoc cref="ZIndexProperty"/>
    public int ZIndex { get => GetValue(ZIndexProperty); set => SetValue(ZIndexProperty, value); }

    /// <inheritdoc cref="OpacityProperty"/>
    public double Opacity { get => GetValue(OpacityProperty); set => SetValue(OpacityProperty, value); }

    /// <inheritdoc cref="ClipToBoundsProperty"/>
    public bool ClipToBounds { get => GetValue(ClipToBoundsProperty); set => SetValue(ClipToBoundsProperty, value); }

    /// <inheritdoc cref="CompositeClipProperty"/>
    public Rect? CompositeClip { get => GetValue(CompositeClipProperty); set => SetValue(CompositeClipProperty, value); }

    /// <inheritdoc cref="RenderOffsetColumnProperty"/>
    public int RenderOffsetColumn { get => GetValue(RenderOffsetColumnProperty); set => SetValue(RenderOffsetColumnProperty, value); }

    /// <inheritdoc cref="RenderOffsetRowProperty"/>
    public int RenderOffsetRow { get => GetValue(RenderOffsetRowProperty); set => SetValue(RenderOffsetRowProperty, value); }

    /// <inheritdoc cref="IsRenderBoundaryProperty"/>
    public bool IsRenderBoundary { get => GetValue(IsRenderBoundaryProperty); set => SetValue(IsRenderBoundaryProperty, value); }

    private static Margins CoerceMargin(UIObject sender, Margins value)
    {
        if (value is { Left: >= 0, Top: >= 0, Right: >= 0, Bottom: >= 0 })
            return value;

        LayoutDiagnostics.Emit(
            LayoutDiagnosticKind.NegativeMarginCoerced, sender as UIElement,
            $"Negative Margin components are unsupported in v1 and were coerced to 0 ({value}); " +
            "use RenderOffsetColumn/Row for overlap effects.");

        return new Margins(
            Math.Max(0, value.Left), Math.Max(0, value.Top),
            Math.Max(0, value.Right), Math.Max(0, value.Bottom));
    }

    // ───────────────────────────── effects routing (invariant 2) ─────────────────────────────
    //
    // This dispatch is the ONLY bridge from property changes to invalidation: the property and
    // styling engines never touch Scene/CellBuffer — they raise typed change notifications, and the
    // element tree routes PropertyEffects metadata here (design doc §5.5).

    /// <inheritdoc/>
    protected override void OnPropertyChanged(in UIPropertyChangedEventArgs args)
    {
        base.OnPropertyChanged(in args);

        if (ReferenceEquals(args.Property, VisibilityProperty))
        {
            // Custom routing (LD5): no PropertyEffects flags are registered for Visibility.
            RouteVisibilityChange(args.GetOldValue<Visibility>(), args.GetNewValue<Visibility>());
            RepairFocusAfterStateInvalidation(); // ND28: hiding the focused element (or an ancestor) repairs focus
            return;
        }

        if (ReferenceEquals(args.Property, IsEnabledProperty))
        {
            UpdateEffectiveEnabled();
            RepairFocusAfterStateInvalidation(); // ND28: after the cascade settles — never mid-walk
            return;
        }

        if (ReferenceEquals(args.Property, ZIndexProperty))
        {
            // The z-order recollect hook: the parent's cached (ZIndex, index) order rebuilds, and
            // sibling boundary layers may reorder in the flat layer list. The generic AffectsRender
            // dispatch below re-rasters the owning zone with the new paint order.
            _visualParent?.InvalidateZOrder();
            GetRenderTree()?.MarkLayersDirty();
        }

        DispatchEffects(args.Property.GetEffects(GetType()));
    }

    /// <inheritdoc/>
    protected internal override void OnInheritedPropertyChanged(in InheritedPropertyChangedEventArgs args)
    {
        base.OnInheritedPropertyChanged(in args);

        // The same dispatch as the ordinary channel (doc §5.5): one root write fans out to
        // inheriting descendants, and each descendant with an effects mapping invalidates itself.
        DispatchEffects(args.Property.GetEffects(GetType()));
    }

    private void DispatchEffects(PropertyEffects effects)
    {
        if ((effects & PropertyEffects.AffectsMeasure) != 0)
            InvalidateMeasure();
        if ((effects & PropertyEffects.AffectsArrange) != 0)
            InvalidateArrange();
        if ((effects & PropertyEffects.AffectsRender) != 0)
            InvalidateVisual();
        if ((effects & PropertyEffects.AffectsComposite) != 0)
            InvalidateComposite();
        if ((effects & PropertyEffects.AffectsParentMeasure) != 0)
            _visualParent?.InvalidateMeasure();
        if ((effects & PropertyEffects.AffectsParentArrange) != 0)
            _visualParent?.InvalidateArrange();
    }

    private void RouteVisibilityChange(Visibility oldValue, Visibility newValue)
    {
        if (oldValue == Visibility.Collapsed || newValue == Visibility.Collapsed)
        {
            // Space is released/reclaimed: self + parent measure via the ancestor walk (LD5).
            InvalidateMeasure();
        }
        else if (IsEffectiveRenderBoundary)
        {
            // Visible ↔ Hidden on a boundary: parameters-only (the empty-clip trick).
            InvalidateComposite();
        }
        else
        {
            // Visible ↔ Hidden on a non-boundary: the zone must repaint (terminal deviation ⑧);
            // layout validity is untouched (LD5).
            InvalidateVisual();
        }
    }
}
