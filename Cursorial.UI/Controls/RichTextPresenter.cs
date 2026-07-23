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

    private record CachedState(int AvailableColumns, RichText Source, FormattedText? Text);

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
            app.CapabilitiesChanged += OnCapabilitiesChanged;

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
            app.CapabilitiesChanged -= OnCapabilitiesChanged;
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
        if (EnsureText(availableSize.Columns) is {} ft)
        {
            return TextAlignment is TextAlignment.Left || LayoutMath.IsUnbounded(availableSize.Columns)
                       ? ft.Size
                       : ft.Size with { Columns = availableSize.Columns };
        }

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
        var bounds = ResolveBounds(possibleColumns);

        var availableColumns = bounds.Columns;
        if (availableColumns is 0)
            return null;

        if (ResolveSource() is not {} text) return null;

        if (_cachedState is { Text: not null } cs &&
            ReferenceEquals(cs.Source, text) &&
            cs.AvailableColumns == availableColumns)
        {
            return cs.Text;
        }

        var tf = new TextFormatter { Trim = TextTrimming, Alignment = TextAlignment };

        var ft = tf.Format(text,
                           availableColumns,
                           capabilities: _subscribedApp?.EffectiveCapabilities.Output,
                           fillEntireBounds: FillEntireBounds,
                           maxRows: bounds is not { Rows: LayoutMath.Unbounded } &&
                                    TextTrimming is not TextTrimming.None
                                        ? bounds.Rows
                                        : null);

        cs = new CachedState(availableColumns, text, ft);

        _cachedState = cs;

        return cs.Text;
    }

    private RichText? ResolveSource()
    {
        RichText? text;

        var source = _cachedState?.Source ?? Source;

        if (source is RichText t)
            text = t;
        else if (source is string s)
            text = ParseRichText(s);
        else
            text = null;

        if (text is not null && _cachedState is null)
            _cachedState = new CachedState(0, text, null);

        return text;
    }

    private Rect ResolveBounds(int? availableColumns)
    {
        if (availableColumns is null)
        {
            Size? desiredSize = HasMeasureConstraint ? LastMeasureConstraint : null;
            Rect? arrangeRect = HasArrangeRect ? LastArrangeRect : null;

            if (_cachedState is { Text: not null } cs && ReferenceEquals(cs.Source, Source))
                availableColumns = cs.AvailableColumns;

            if (desiredSize is { Columns: var desiredColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, desiredColumns) : desiredColumns;

            if (arrangeRect is { Columns: var arrangeColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, arrangeColumns) : arrangeColumns;
        }

        var bounds = Bounds;

        return (bounds with { Columns = availableColumns ?? bounds.Columns }).ToRect();
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
                             DefaultStyle = style,
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