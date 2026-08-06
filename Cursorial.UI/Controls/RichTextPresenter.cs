using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2L-1) that hosts <see cref="RichText"/>, painted via
/// <see cref="RenderContext.DrawFormattedText(FormattedText, in Rect, IBrush, TextAttributes, UnderlineStyle)"/>.
/// </summary>
public sealed class RichTextPresenter : DrawnContentPresenter
{
    /// <summary>The <see cref="RichText">rich text</see> to render or <see cref="TextMarkup.Parse(string)">markup
    /// to parse</see>; (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<object?> SourceProperty =
        UIProperty.Register<RichTextPresenter, object?>(nameof(Source),
                                                        changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The <see cref="Rendering.Text.TextTrimming">text trimming</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        UIProperty.Register<RichTextPresenter, TextTrimming>(nameof(TextTrimming),
                                                             defaultValue: TextTrimming.CharacterEllipsis,
                                                             changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The <see cref="Rendering.Text.TextAlignment">text alignment</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<RichTextPresenter, TextAlignment>(nameof(TextAlignment),
                                                              changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The <see cref="FormattedText.FillEntireBounds" /> value to set when formatting the rich text.</summary>
    public static readonly StyledProperty<bool> FillEntireBoundsProperty =
        UIProperty.Register<RichTextPresenter, bool>(nameof(FillEntireBounds),
                                                     defaultValue: false,
                                                     changed: OnLayoutAffectingPropertyChanged);

    /// <inheritdoc cref="TextElement.ForegroundProperty"/>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RichTextPresenter>();

    private record CachedState(int AvailableColumns, RichText Source, FormattedText? Text, int? MaxRows = null)
    {
        /// <summary>Whether the row budget this layout was formatted under actually truncated it —
        /// only then does a taller slot invalidate the cache (the grow-back path).</summary>
        public bool RowCapBit => MaxRows is {} cap && Text is { Size.Rows: var rows } && rows >= cap;
    }

    private CachedState? _cachedState;
    private UIApplication? _subscribedApp;

    static RichTextPresenter()
    {
        ForegroundProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<IBrush?>(Changed: OnRenderAffectingPropertyChanged)
        );
    }

    /// <inheritdoc cref="SourceProperty"/>
    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <inheritdoc cref="TextTrimmingProperty"/>
    public TextTrimming TextTrimming
    {
        get => GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }


    /// <inheritdoc cref="TextAlignmentProperty"/>
    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    /// <inheritdoc cref="FillEntireBoundsProperty"/>
    public bool FillEntireBounds
    {
        get => GetValue(FillEntireBoundsProperty);
        set => SetValue(FillEntireBoundsProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc/>
    protected override bool IsPrimaryContentVisible
    {
        get
        {
            if (ResolveSource() is not {} source)
                return false;

            OutputCapabilities? outputCaps = null;

            foreach (var block in source.Blocks)
            {
                if (block is SizedTextBlock stb)
                {
                    outputCaps ??= _subscribedApp?.EffectiveCapabilities.Output ?? OutputCapabilities.None;

                    if (stb.Fallback is null && stb.Sizing.IsSupported(outputCaps) is false)
                        return false;
                }
            }

            return true;
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);

        // Re-evaluate image-vs-placeholder when the terminal renegotiates graphics support (the measure cache would
        // otherwise leave the placeholder visibility / :placeholder stale on a caps flip — CD-P2K-1 audit).
        if (UIApplication.Current is {} app)
        {
            app.EffectiveCapabilitiesChanged += OnCapabilitiesChanged;

            app.CapabilityOverridesChanged +=
                OnCapabilityOverridesChanged; // FB-5: forced-off images collapse to the placeholder live

            _subscribedApp = app;
        }
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        if (_subscribedApp is {} app)
        {
            app.EffectiveCapabilitiesChanged -= OnCapabilitiesChanged;
            app.CapabilityOverridesChanged -= OnCapabilityOverridesChanged;
            _subscribedApp = null;
        }

        base.OnDetachedFromTree(in e);
    }

    private void OnCapabilitiesChanged(object? sender, CapabilitiesChangedEventArgs e)
    {
        InvalidateContent(
            invalidateMeasure:
            true); // defeats the measure-cache early-out so MeasureOverride re-runs UpdatePlaceholderState
    }

    private void OnCapabilityOverridesChanged(object? sender, EventArgs e)
    {
        InvalidateContent(
            invalidateMeasure:
            true); // defeats the measure-cache early-out so MeasureOverride re-runs UpdatePlaceholderState
    }

    /// <inheritdoc/>
    protected override Size MeasurePrimaryContent(Size availableSize)
    {
        var wasMarkedTrimmed = GetValueSource(TextBlock.IsTrimmedProperty) is
                               {
                                   Kind: ValueSourceKind.Default,
                                   IsCurrentValue: true
                               };

        if (EnsureText(availableSize.Columns) is {} ft)
        {
            if (ft.HasTrimmedLines)
                SetCurrentValue(TextBlock.IsTrimmedPropertyKey, true);
            else if (wasMarkedTrimmed)
                ClearValue(TextBlock.IsTrimmedPropertyKey);

            return TextAlignment is TextAlignment.Left || LayoutMath.IsUnbounded(availableSize.Columns)
                       ? ft.Size
                       : ft.Size with { Columns = availableSize.Columns };
        }

        if (wasMarkedTrimmed)
            ClearValue(TextBlock.IsTrimmedPropertyKey);

        return Size.Empty;
    }

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if (EnsureText(context.Bounds.Columns) is {} ft && TextElement.GetForeground(this) is {} fg)
        {
            var bounds = context.Bounds;
            var attributes = TextElement.ComposeAttributes(this);

            context.DrawFormattedText(ft, bounds, fg, attributes.Flags, attributes.UnderlineShape);
        }
    }

    private FormattedText? EnsureText(int? possibleColumns)
    {
        if (ResolveSource() is not { IsEmpty: false } text) return null;

        var bounds = ResolveBounds(possibleColumns);

        var availableColumns = bounds.Columns;
        if (availableColumns is 0)
            return null;

        if (_cachedState is { Text.Blocks.Length: > 0 } cs &&
            ReferenceEquals(cs.Source, text) &&
            cs.AvailableColumns == availableColumns)
        {
            return cs.Text;
        }

        var maxRows = bounds is not { Rows: 0 or LayoutMath.Unbounded } && TextTrimming is not TextTrimming.None
                          ? bounds.Rows
                          : (int?)null;

        var ft = Format(text, availableColumns, null, TextTrimming, maxRows: maxRows);

        cs = new CachedState(availableColumns, text, ft, maxRows);

        _cachedState = cs;

        return cs.Text;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // Reformat when the slot shrank below the layout — or GREW past a row cap that actually
        // truncated it: reusing a capped layout for a taller slot is how text used to stay
        // trimmed forever after the space it needed came back.
        if (_cachedState is { Text.Size.Rows: var rows } cs &&
            (rows > finalSize.Rows || (cs.RowCapBit && finalSize.Rows > cs.MaxRows)))
        {
            _cachedState = null;
            result = MeasurePrimaryContent(finalSize);
        }

        return result;
    }

    private FormattedText Format(RichText text,
                                 int width,
                                 OutputCapabilities? caps,
                                 TextTrimming? trimmingOverride = null,
                                 WrapMode? wrappingOverride = null,
                                 int? maxRows = null)
    {
        var bounds = ResolveBounds(width);
        if (bounds.Columns is 0)
            return FormattedText.Empty;

        var textTrimming = trimmingOverride ?? TextTrimming;

        var tf = new TextFormatter
                 {
                     Alignment = TextAlignment,
                     Trim = textTrimming,
                     Wrap = wrappingOverride ?? TextFormatter.DefaultWrap
                 };

        if (text.IsEmpty)
            return FormattedText.Empty;

        var ft = tf.Format(text,
                           width,
                           capabilities: caps ?? _subscribedApp?.EffectiveCapabilities.Output,
                           maxRows: maxRows);

        return ft;
    }

    internal string? GetUntrimmedText(int maxWidth)
    {
        if (ResolveSource() is not {} text) return null;

        if (text.IsEmpty) return null;

        var bounds = ResolveBounds(maxWidth);
        if (bounds.Columns is 0)
            return null;

        var tf = new TextFormatter
                 {
                     Alignment = TextAlignment.Left,
                     Trim = TextTrimming.None,
                     Wrap = WrapMode.CharacterWrap
                 };

        // Deliberately UNCAPPED rows: this is the trimmed-content tooltip's payload, and its whole
        // job is to reveal what the presenter's own bounds hid — capping it at those bounds would
        // re-hide exactly the lines the user hovered to see. Display limits belong to the tooltip.
        return tf.FormatPlainText(text, maxWidth, maxRows: null);
    }

    private RichText? ResolveSource()
    {
        RichText? text;

        var source = _cachedState?.Source ?? Source;

        if (source is RichText { IsEmpty: false } t)
            text = t;
        else if (source is string { Length: > 0 } s)
            text = ParseRichText(s);
        else
            text = null;

        if (text is not null && _cachedState is null)
            _cachedState = new CachedState(0, text, null);

        return text;
    }

    /// <summary>
    /// The column / row budget to format against. A <see cref="Size"/>, not a <see cref="Rect"/>: every
    /// caller reads only the extents, and narrowing <see cref="UIElement.Bounds"/> through
    /// <c>LayoutRect.ToRect()</c> to carry an origin nobody looks at made a negative left/top margin
    /// throw. <c>Bounds</c> is a <c>LayoutRect</c> exactly so it can hold a negative origin (LD19);
    /// <c>ToRect</c> is the narrowing affordance for a caller that has PROVEN a non-negative one, and
    /// this is not such a caller.
    /// </summary>
    private Size ResolveBounds(int? availableColumns)
    {
        Rect? arrangeRect = HasArrangeRect ? LastArrangeRect : null;

        if (availableColumns is null)
        {
            Size? desiredSize = HasMeasureConstraint ? LastMeasureConstraint : null;

            if (_cachedState is { Text: not null } cs && ReferenceEquals(cs.Source, Source))
                availableColumns = cs.AvailableColumns;

            if (desiredSize is { Columns: var desiredColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, desiredColumns) : desiredColumns;

            if (arrangeRect is { Columns: var arrangeColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, arrangeColumns) : arrangeColumns;
        }

        var bounds = Bounds;
        var rows = bounds.Rows is 0 && HasArrangeRect ? LastArrangeRect.Rows : bounds.Rows;

        return new Size(Math.Min(availableColumns ?? bounds.Columns, LayoutMath.MaxExtent), rows);
    }

    private RichText ParseRichText(string s)
    {
        var attributes = TextElement.ComposeAttributes(this);
        var fg = Foreground ?? Brushes.Default;
        var fgColor = fg is SolidColorBrush { Color: var c } ? c : Color.Default;

        var style = CellStyle.Transparent
                             .WithForeground(fgColor)
                             .WithAttributes(attributes.Flags)
                             .WithUnderlineStyle(attributes.UnderlineShape);

        if (attributes.Flags.HasFlag(TextAttributes.Underline))
            style = style.WithUnderlineColor(fgColor);

        var rtb = new RichTextBuilder(style);

        TextMarkup.Parse(s, rtb,
                         new TextMarkupOptions
                         {
                             BrushResolver = ResourceBrushResolver.Create(this),
                             DefaultStyle = style
                         });

        return rtb.Build();
    }

    private void InvalidateContent(bool invalidateMeasure = false)
    {
        _cachedState = null;

        if (invalidateMeasure)
            InvalidateMeasure();

        InvalidateVisual();
    }

    private static void OnRenderAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as RichTextPresenter)?.InvalidateContent(invalidateMeasure: false);

    private static void OnLayoutAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as RichTextPresenter)?.InvalidateContent(invalidateMeasure: true);
}