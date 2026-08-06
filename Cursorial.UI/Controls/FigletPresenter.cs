using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
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

    /// <summary>The <see cref="Rendering.Text.TextTrimming">text trimming</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        UIProperty.Register<FigletPresenter, TextTrimming>(nameof(TextTrimming),
                                                           changed: OnRenderAffectingPropertyChanged);

    /// <summary>The <see cref="Rendering.Text.TextAlignment">text alignment</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<FigletPresenter, TextAlignment>(nameof(TextAlignment),
                                                            changed: OnRenderAffectingPropertyChanged);

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

    private record CachedState(int AvailableColumns, string Text, FormattedText RealizedText);

    private CachedState? _cachedState;
    private UIApplication? _subscribedApp;

    static FigletPresenter()
    {
        ForegroundProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<IBrush?>(Changed: OnRenderAffectingPropertyChanged)
        );
    }

    /// <inheritdoc cref="TextProperty"/>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <inheritdoc cref="TextTrimmingProperty"/>
    public TextTrimming? TextTrimming
    {
        get => GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }


    /// <inheritdoc cref="TextAlignmentProperty"/>
    public TextAlignment? TextAlignment
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
        if (EnsureText(availableSize.Columns) is {} ft)
            return ft.Size;

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

        if (_cachedState is {} cs &&
            Equals(cs.Text, Text) &&
            cs.AvailableColumns == availableColumns)
        {
            return cs.RealizedText;
        }

        var tf = new TextFormatter
                 {
                     Trim = TextTrimming ?? Rendering.Text.TextTrimming.ClipFromEnd,
                     Alignment = TextAlignment ?? Rendering.Text.TextAlignment.Left
                 };

        var font = Font ?? FigletFonts.Small;
        var rtb = new RichTextBuilder();
        var style = ResolveStyle();

        foreach (var line in text.Split(LineSeparators))
            rtb.Figlet(line, font, style, TextAlignment, Padding);

        var richText = rtb.Build();

        var ft = tf.Format(richText,
                           availableColumns,
                           capabilities: _subscribedApp?.EffectiveCapabilities.Output,
                           fillEntireBounds: FillEntireBounds);

        cs = new CachedState(availableColumns, text, ft);

        _cachedState = cs;

        return cs.RealizedText;
    }

    /// <summary>
    /// The column / row budget to format against. A <see cref="Size"/>, not a <see cref="Rect"/>: the
    /// only caller reads <c>Columns</c>, so clamping the origin to zero just to be able to narrow
    /// <see cref="UIElement.Bounds"/> through <c>LayoutRect.ToRect()</c> carried a coordinate nobody
    /// looks at. (<c>Bounds</c> is a <c>LayoutRect</c> precisely so it can hold the negative origin a
    /// negative left/top margin produces — LD19.)
    /// </summary>
    private Size ResolveBounds(int? availableColumns)
    {
        if (availableColumns is null)
        {
            Size? desiredSize = HasMeasureConstraint ? LastMeasureConstraint : null;
            Rect? arrangeRect = HasArrangeRect ? LastArrangeRect : null;

            if (_cachedState is {} cs && ReferenceEquals(cs.Text, Text))
                availableColumns = cs.AvailableColumns;

            if (desiredSize is { Columns: var desiredColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, desiredColumns) : desiredColumns;

            if (arrangeRect is { Columns: var arrangeColumns })
                availableColumns = availableColumns is {} c ? Math.Min(c, arrangeColumns) : arrangeColumns;
        }

        var bounds = Bounds;

        return new Size(Math.Min(availableColumns ?? bounds.Columns, LayoutMath.MaxExtent),
                        Math.Min(bounds.Rows, LayoutMath.MaxExtent));
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