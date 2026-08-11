using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// A primitive (design doc §12 / CD-P2L-1) that hosts content presented in a <see cref="FigletFont">figlet font</see>,
/// painted via <see cref="RenderContext.DrawFormattedText(FormattedText, in Rect, in BrushedStyle)"/>.
/// </summary>
public sealed class FigletPresenter : DrawnContentPresenter, ITrimmedTextSource
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

    // The shared parse/format cache (UNIFIED-TEXT-SCOPING Scope A). Its key carries the
    // (ResourceVersion, ActualThemeVariant) parse-freshness terms this presenter's hand-rolled
    // CachedState LACKED (doc defect 3): the baked ResolveStyle() flattens the theme-reactive
    // Foreground default into the parse, so a variant flip must invalidate it — pull-based,
    // through the key (sealed dictionaries never pulse — CD16). Created LAZILY: base-constructor
    // property plumbing (ClipToBounds coercion) may run before this type's constructor body.
    private FormattedTextCache? _cache;

    private FormattedTextCache Cache
        => _cache ??= new FormattedTextCache(this, () => InvalidateContent(invalidateMeasure: true));

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

        // #19a: Padding is a PARSE input (rtb.Figlet(..., Padding) — the figlet blocks' stacking
        // margins) with no cache-key term, so its freshness is push-based like Font's. Without
        // this callback a padding change re-measured into a cache HIT and the stale parse (the
        // old block margins) laid out forever.
        PaddingProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<Margins>(PaddingProperty.DefaultMetadata.DefaultValue,
                                          Changed: OnLayoutAffectingPropertyChanged)
        );

        ForegroundProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<IBrush?>(ForegroundProperty.DefaultMetadata.DefaultValue,
                                          Changed: OnRenderAffectingPropertyChanged)
        );

        // Unlike some other DrawnContentPresenters, we know figlets don't rely on fragments, so there's
        // no need to ever forcibly coerce ClipToBounds to true.
        ClipToBoundsProperty.OverrideMetadata<FigletPresenter>(
            new PropertyMetadata<bool>(
                DefaultValue: false,
                Coerce: static (_, b) => b)
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
        // The cache's callback routes through InvalidateContent(invalidateMeasure: true), which defeats the
        // measure-cache early-out so MeasureOverride re-runs UpdatePlaceholderState; FB-5 forced-off images
        // collapse to the placeholder live via the overrides subscription.
        Cache.Attach();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        Cache.Detach();
        base.OnDetachedFromTree(in e);
    }

    /// <inheritdoc/>
    protected override Size MeasurePrimaryContent(Size availableSize)
        => FormattedTextCache.MeasureAndAdvertiseTrimmed(this, EnsureText(availableSize.Columns));

    /// <inheritdoc/>
    protected override void RenderPrimaryContent(RenderContext context)
    {
        if (EnsureText(context.Bounds.Columns) is {} ft)
        {
            // M3: a null resolved foreground paints with Brushes.Default (the terminal-default
            // ink) instead of skipping the draw — the default is theme-key-backed, so a null only
            // arises when an application states one, and "text vanishes" was never the intent.
            var fg = TextElement.GetForeground(this) ?? Brushes.Default;
            var bounds = context.Bounds;
            var attributes = TextElement.ComposeAttributes(this);

            context.DrawFormattedText(ft, bounds,
                                      new BrushedStyle { Foreground = fg }
                                          .Imposing(attributes.Flags, attributes.UnderlineShape));
        }
    }

    private FormattedText? EnsureText(int? possibleColumns, int? arrangedRows = null)
    {
        if (Text is not { Length: > 0 } text)
            return null;

        var bounds = Cache.ResolveBounds(possibleColumns, text);

        var availableColumns = bounds.Columns;
        if (availableColumns is 0)
            return null;

        // Bounds publishes AFTER ArrangeOverride runs, so the arrange-time reformat passes the
        // fresh row budget explicitly — reading Bounds.Rows there re-formats under the OLD cap
        // and the layout (and the IsTrimmed flag) never grows back.
        var rows = arrangedRows ?? bounds.Rows;
        var maxRows = rows is not (0 or LayoutMath.Unbounded) ? rows : (int?) null;

        var request = new FormattedTextCache.LayoutRequest(
            text, MarkupLane: false, availableColumns, maxRows,
            TextWrapping, TextAlignment, TextTrimming);

        if (Cache.TryGetLayout(in request, out var cached))
            return cached;

        return Cache.FormatAndStore(in request, BuildRichText(text));
    }

    private RichText BuildRichText(string text)
    {
        var font = Font ?? FigletFonts.Small;

        var rtb = new RichTextBuilder();
        var style = ResolveStyle();

        // The shared splitter folds \r\n into ONE break (D12): a CRLF used to yield a phantom empty
        // figlet block per break. Genuinely empty segments (a blank line, "a\n\nb") stay — one
        // empty block per blank line is content.
        foreach (var range in HardLineBreaks.EnumerateLines(text))
            rtb.Figlet(text[range], font, style, TextAlignment, Padding);

        var richText = rtb.Build();
        return richText;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);

        // Reformat when the slot shrank below the layout — or GREW past a row cap that actually
        // truncated it: reusing a capped layout for a taller slot is how text used to stay
        // trimmed forever after the space it needed came back. The fresh finalSize budget is
        // passed explicitly (Bounds still holds the PREVIOUS arrange here).
        if (Cache.NeedsRowBudgetReformat(finalSize.Rows))
        {
            Cache.Invalidate();
            result = FormattedTextCache.MeasureAndAdvertiseTrimmed(
                this, EnsureText(finalSize.Columns, finalSize.Rows));
        }

        return result;
    }

    internal string? GetUntrimmedText(int maxWidth)
    {
        if (Text is not { Length: > 0 } text) return null;

        var bounds = Cache.ResolveBounds(maxWidth, text);
        if (bounds.Columns is 0)
            return null;

        if (BuildRichText(text) is not { IsEmpty: false } richText)
            return null;

        // This presenter's untrimmed heritage, stated (M4): Trim=None under CharacterWrap —
        // TextBlock and the access-text payload state CharacterEllipsis, the shared default.
        return Cache.FormatUntrimmedPlainText(richText, maxWidth, TextTrimming.None, WrapMode.CharacterWrap);
    }

    string? ITrimmedTextSource.GetUntrimmedText(int maxWidth) => GetUntrimmedText(maxWidth);

    private CellStyle ResolveStyle()
    {
        var attributes = TextElement.ComposeAttributes(this);
        var fg = Foreground ?? Brushes.Default;
        var fgColor = fg is SolidColorBrush { Color: var c } ? c : Color.Default;

        // No foreground on the block style — the element brush rides the paint preference
        // (RenderPrimaryContent), which colors the face wherever the document declares nothing. A
        // flattened solid here would be a block-level declaration out-ranking it: same color for a
        // solid element brush, but the wrong level speaking — and a gradient element brush now spans
        // the painted bounds rather than restarting per line-block.
        var style = CellStyle.Transparent
                             .WithForeground(Color.Default)
                             .WithAttributes(attributes.Flags)
                             .WithUnderlineStyle(attributes.UnderlineShape);

        // The underline color has no preference rung, so the flattened element color still rides the
        // block style — unchanged behavior, no competition to misrank.
        if (attributes.Flags.HasFlag(TextAttributes.Underline))
            style = style.WithUnderlineColor(fgColor);

        return style;
    }

    private void InvalidateContent(bool invalidateMeasure = false)
    {
        Cache.Invalidate();

        if (invalidateMeasure)
            InvalidateMeasure();

        InvalidateVisual();
    }

    private static void OnRenderAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as FigletPresenter)?.InvalidateContent(invalidateMeasure: false);

    private static void OnLayoutAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as FigletPresenter)?.InvalidateContent(invalidateMeasure: true);
}