using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;

using CellStyle = Cursorial.Output.Style;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2L-1) that hosts content presented in a <see cref="FigletFont">figlet font</see>,
/// painted via <see cref="RenderContext.DrawFormattedText(FormattedText, in Rect, IBrush, TextAttributes, UnderlineStyle)"/>.
/// </summary>
public sealed class FigletPresenter : DrawnContentPresenter
{
    /// <summary>The figlet text to render or <see cref="TextMarkup.Parse(string)">markup
    /// to parse</see>; (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<FigletPresenter, string?>(nameof(Text),
                                                      changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The <see cref="Rendering.Text.TextTrimming">text trimming</see> to apply to the figlet text.</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        TextElement.TextTrimmingProperty.AddOwner<FigletPresenter>();

    /// <summary>The <see cref="Rendering.Text.WrapMode">text wrapping</see> to apply to the figlet text.</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        TextElement.TextWrappingProperty.AddOwner<FigletPresenter>();

    /// <summary>The <see cref="Rendering.Text.TextAlignment">text alignment</see> to apply to the figlet text.</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<FigletPresenter, TextAlignment>(nameof(TextAlignment));

    /// <summary>
    /// The <see cref="FormattedText.FillEntireBounds" /> value to set when formatting the figlet text.
    /// </summary>
    public static readonly StyledProperty<bool> FillEntireBoundsProperty =
        UIProperty.Register<FigletPresenter, bool>(nameof(FillEntireBounds),
                                                   defaultValue: false,
                                                   changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The figlet font to use when rendering the text.</summary>
    public static readonly StyledProperty<FigletFont?> FontProperty =
        UIProperty.Register<FigletPresenter, FigletFont?>(nameof(Font),
                                                          changed: OnLayoutAffectingPropertyChanged);

    /// <inheritdoc cref="Border.PaddingProperty" />
    public static readonly StyledProperty<Margins> PaddingProperty =
        Border.PaddingProperty.AddOwner<FigletPresenter>();

    /// <inheritdoc cref="TextElement.ForegroundProperty"/>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<FigletPresenter>();

    private static readonly char[] LineSeparators = ['\r','\n'];

    private record CachedState(int AvailableColumns, string Text, FormattedText? RealizedText, int? MaxRows = null)
    {
        /// <summary>Whether the row budget this layout was formatted under actually truncated it —
        /// only then does a taller slot invalidate the cache (the grow-back path).</summary>
        public bool RowCapBit => MaxRows is {} cap && RealizedText is { Size.Rows: var rows } && rows >= cap;
    }

    private CachedState? _cachedState;
    private UIApplication? _subscribedApp;

    static FigletPresenter()
    {
        AffectsMeasure<FigletPresenter>(FontProperty, TextAlignmentProperty);
        AffectsRender<FigletPresenter>(FontProperty);

        TextTrimmingProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<TextTrimming>(TextTrimmingProperty.DefaultMetadata.DefaultValue,
                                               Changed: OnLayoutAffectingPropertyChanged)
        );

        TextWrappingProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<WrapMode>(WrapMode.WordWrap,
                                           Changed: OnLayoutAffectingPropertyChanged)
        );

        ForegroundProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<IBrush?>(ForegroundProperty.DefaultMetadata.DefaultValue,
                                          Changed: OnRenderAffectingPropertyChanged)
        );
    }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="TextTrimmingProperty"/>
    public TextTrimming TextTrimming
    {
        get => GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    /// <inheritdoc cref="TextWrappingProperty"/>
    public WrapMode TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
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

    /// <inheritdoc cref="ForegroundProperty"/>
    public FigletFont? Font
    {
        get => GetValue(FontProperty);
        set => SetValue(FontProperty, value);
    }

    /// <inheritdoc cref="PaddingProperty"/>
    public Margins Padding
    {
        get => GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <inheritdoc/>
    protected override bool IsPrimaryContentVisible => Text is not null;

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

            return ft.Size;
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
        if (Text is not { Length: > 0 } text)
            return null;

        var bounds = ResolveBounds(possibleColumns);

        var availableColumns = bounds.Columns;
        if (availableColumns is 0)
            return null;

        if (_cachedState is { RealizedText.Blocks.Length: > 0 } cs &&
            Equals(cs.Text, Text) &&
            cs.AvailableColumns == availableColumns)
        {
            return cs.RealizedText;
        }

        var maxRows = bounds is not { Rows: 0 or LayoutMath.Unbounded } ? bounds.Rows : (int?) null;

        var richText = BuildRichText(text);

        var ft = Format(richText, availableColumns, null, TextTrimming, TextWrapping, maxRows: maxRows);

        cs = new CachedState(availableColumns, text, ft, maxRows);

        _cachedState = cs;

        return cs.RealizedText;
    }

    private RichText BuildRichText(string text)
    {
        var font = Font ?? FigletFonts.Small;

        var rtb = new RichTextBuilder();
        var style = ResolveStyle();

        foreach (var line in text.Split(LineSeparators))
            rtb.Figlet(line, font, style, TextAlignment, Padding);

        var richText = rtb.Build();
        return richText;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // Reformat when the slot shrank below the layout — or GREW past a row cap that actually
        // truncated it: reusing a capped layout for a taller slot is how text used to stay
        // trimmed forever after the space it needed came back.
        if (_cachedState is { RealizedText.Size.Rows: var rows } cs &&
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
        if (Text is not { Length: > 0 } text) return null;

        var bounds = ResolveBounds(maxWidth);
        if (bounds.Columns is 0)
            return null;

        if (BuildRichText(text) is not { IsEmpty: false } richText)
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
        return tf.FormatPlainText(richText, maxWidth, maxRows: null);
    }

    private LayoutRect ResolveBounds(int? availableColumns)
    {
        LayoutRect? arrangeRect = HasArrangeRect ? LastArrangeRect : null;

        if (availableColumns is null)
        {
            Size? desiredSize = HasMeasureConstraint ? LastMeasureConstraint : null;

            if (_cachedState is { RealizedText: not null } cs && cs.Text == Text)
                availableColumns = cs.AvailableColumns;

            if (desiredSize is { Columns: var desiredColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, desiredColumns) : desiredColumns;

            if (arrangeRect is { Columns: var arrangeColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, arrangeColumns) : arrangeColumns;
        }

        var bounds = Bounds;
        var rows = bounds.Rows is 0 && HasArrangeRect ? LastArrangeRect.Rows : bounds.Rows;

        return bounds with
               {
                   Columns = Math.Min(availableColumns ?? bounds.Columns, LayoutMath.MaxExtent),
                   Rows = rows
               };
    }

    private CellStyle ResolveStyle()
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

        return style;
    }

    private void InvalidateContent(bool invalidateMeasure = false)
    {
        _cachedState = null;

        if (invalidateMeasure)
            InvalidateMeasure();

        InvalidateVisual();
    }

    private static void OnRenderAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as FigletPresenter)?.InvalidateContent(invalidateMeasure: false);

    private static void OnLayoutAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as FigletPresenter)?.InvalidateContent(invalidateMeasure: true);
}