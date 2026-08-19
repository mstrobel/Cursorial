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
/// A primitive (design doc §12 / CD-P2L-1) that hosts <see cref="RichText"/>, painted via
/// <see cref="RenderContext.DrawFormattedText(FormattedText, in Rect, in BrushedStyle)"/>.
/// </summary>

public sealed class RichTextPresenter : DrawnContentPresenter, ITrimmedTextSource
{
    /// <summary>The <see cref="RichText">rich text</see> to render or <see cref="TextMarkup.Parse(string)">markup
    /// to parse</see>; (<see langword="null"/> = none ⇒ the placeholder shows).</summary>
    public static readonly StyledProperty<object?> SourceProperty =
        UIProperty.Register<RichTextPresenter, object?>(nameof(Source),
                                                        changed: OnLayoutAffectingPropertyChanged);

    /// <summary>The <see cref="Rendering.Text.TextTrimming">text trimming</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        TextElement.TextTrimmingProperty.AddOwner<RichTextPresenter>();

    /// <summary>The <see cref="Rendering.Text.WrapMode">text wrapping</see> to apply to the rich text.</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        TextElement.TextWrappingProperty.AddOwner<RichTextPresenter>();

    /// <inheritdoc cref="TextElement.FontProperty"/>
    public static readonly StyledProperty<IGlyphFont?> FontProperty =
        TextElement.FontProperty.AddOwner<RichTextPresenter>();

    /// <inheritdoc cref="TextElement.SizingProperty"/>
    public static readonly StyledProperty<TextSizing> SizingProperty =
        TextElement.SizingProperty.AddOwner<RichTextPresenter>();

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

    // The shared parse/format cache (UNIFIED-TEXT-SCOPING Scope A): it owns the cache slots, the
    // freshness key — including the (ResourceVersion, ActualThemeVariant) parse terms parsing a
    // string Source through ResourceBrushResolver requires (resolution is static-per-parse and the
    // parse is sticky, so without them a variant flip repaints the pre-flip ink forever — CD16) —
    // the caps subscriptions, and the ResolveBounds arithmetic. Created LAZILY: the base
    // DrawnContentPresenter constructor coerces ClipToBounds, which consults
    // IsPrimaryContentVisible → ResolveSource before this type's constructor body could run.
    // Internal (not private) so tests can observe the format counters (the TextBlock precedent).
    internal FormattedTextCache Cache
        => field ??= new FormattedTextCache(this, () => InvalidateContent(invalidateMeasure: true));

    static RichTextPresenter()
    {
        ForegroundProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<IBrush?>(Changed: OnRenderAffectingPropertyChanged)
        );

        TextTrimmingProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<TextTrimming>(TextTrimmingProperty.DefaultMetadata.DefaultValue,
                                               Changed: OnLayoutAffectingPropertyChanged)
        );

        TextWrappingProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<WrapMode>(DefaultValue: WrapMode.WordWrap,
                                           Changed: OnLayoutAffectingPropertyChanged)
        );

        FontProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<IGlyphFont?>(FontProperty.DefaultMetadata.DefaultValue,
                                              Changed: OnLayoutAffectingPropertyChanged)
        );

        SizingProperty.OverrideMetadata<RichTextPresenter>(
            new PropertyMetadata<TextSizing>(SizingProperty.DefaultMetadata.DefaultValue,
                                             Changed: OnLayoutAffectingPropertyChanged)
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

    /// <inheritdoc cref="FontProperty"/>
    public IGlyphFont? Font
    {
        get => GetValue(FontProperty);
        set => SetValue(FontProperty, value);
    }

    /// <inheritdoc cref="SizingProperty"/>
    public TextSizing Sizing
    {
        get => GetValue(SizingProperty);
        set => SetValue(SizingProperty, value);
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
                    outputCaps ??= Cache.OutputCapabilities ?? OutputCapabilities.None;

                    if (stb is { Fallback: null, Sizing: { IsNormal: false } sz } &&
                        sz.IsSupported(outputCaps) is false)
                    {
                        return false;
                    }
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
        // Format against THIS measure's row constraint, not the stale previous-arrange Bounds.Rows: a
        // font-size change re-measures with a different row budget, and deriving rows from Bounds meant
        // the text kept trimming to the OLD budget. Unbounded rows (a measure-to-content parent) map to
        // a natural, untrimmed format; the arrange pass then constrains to the final slot.
        => FormattedTextCache.MeasureAndAdvertiseTrimmed(this, EnsureText(availableSize.Columns, availableSize.Rows));

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

    private FormattedText? EnsureText(int? possibleColumns, int? rowBudget = null)
    {
        if (ResolveSource() is not { IsEmpty: false } text) return null;

        var bounds = Cache.ResolveBounds(possibleColumns, Source);

        var availableColumns = bounds.Columns;
        if (availableColumns is 0)
            return null;

        // The row budget is passed explicitly by measure (its constraint) and arrange (the final slot),
        // both FRESH. Only render falls back to Bounds.Rows — and render runs AFTER arrange, so Bounds is
        // current there. The stale trap is reading Bounds.Rows before arrange publishes it: that re-formats
        // under the OLD budget and the layout (and the IsTrimmed flag) never tracks a size change.
        var rows = rowBudget ?? bounds.Rows;
        var maxRows = rows is not (0 or LayoutMath.Unbounded) ? rows : (int?)null;

        var request = new FormattedTextCache.LayoutRequest(
            text, MarkupLane: false, availableColumns, maxRows,
            TextWrapping, TextAlignment, TextTrimming,
            FillBounds: FillEntireBounds);

        if (Cache.TryGetLayout(in request, out var cached))
            return cached;

        return Cache.FormatAndStore(in request, text);
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
        if (ResolveSource() is not { IsEmpty: false } text) return null;

        var bounds = Cache.ResolveBounds(maxWidth, Source);
        if (bounds.Columns is 0)
            return null;

        // This presenter's untrimmed heritage, stated (M4): Trim=None under CharacterWrap —
        // TextBlock and the access-text payload state CharacterEllipsis, the shared default.
        return Cache.FormatUntrimmedPlainText(text, maxWidth, TextTrimming.None, WrapMode.CharacterWrap);
    }

    string? ITrimmedTextSource.GetUntrimmedText(int maxWidth) => GetUntrimmedText(maxWidth);

    private RichText? ResolveSource()
    {
        // A cached parse is only good for the resources/variant it was resolved against (§11.6): the
        // cache drops it when either moves, so the next read re-parses `Source` and re-resolves its
        // [fg=…]/[bg=…] spans. The parse is sticky — the cached document shadows `Source` on every
        // later read until a property change or freshness move drops it.
        if (Cache.GetDocument() is { } document)
            return document;

        RichText? text;

        if (Source is RichText { IsEmpty: false } t)
            text = t;
        else if (Source is string { Length: > 0 } s)
            text = ParseRichText(s);
        else
            text = null;

        if (text is not null)
            Cache.StoreDocument(text);

        return text;
    }

    private RichText ParseRichText(string s)
    {
        var attributes = TextElement.ComposeAttributes(this);
        var fg = Foreground ?? Brushes.Default;
        var fgColor = fg is SolidColorBrush { Color: var c } ? c : Color.Default;

        // The document default declares NO foreground — the element brush rides the paint preference
        // (RenderPrimaryContent), the ladder's rung for exactly this opinion, and colors every run no
        // level of the document declared one for. Stating a flattened solid here instead would out-rank
        // the preference at the document rung: same color for a solid element brush, but the wrong
        // level speaking.
        //
        // The composed ATTRIBUTES (and their underline shape) are likewise NOT baked here — they
        // impose at paint through the preference (RenderPrimaryContent's Imposing, TextBlock's
        // shape; maintainer ruling 2026-08-11 #4 resolving D2). The parse is sticky and the axis
        // properties ride the global AffectsRender lane with no cache term, so a baked flag
        // removed after the parse survived in the document default forever; imposed at paint, an
        // attribute-only flip is honored by a repaint by construction.
        // The carrier the pipeline actually takes — the BrushedStyle FromStated(CellStyle.Transparent
        // .WithForeground(Default)) used to hand it: a transparent background and underline colour, no
        // foreground (it rides the paint preference), no baked attributes (they impose at paint).
        var style = new BrushedStyle
                    {
                        Background = Brushes.Transparent,
                        UnderlineColor = Brushes.Transparent
                    };

        // The underline COLOR stays parse-side (the ruling's delegated choice; criterion:
        // behaviour-preservation for current inputs): it has no preference rung of its own in the
        // resolver, so the flattened element color keeps riding the document default — unchanged
        // bytes, no competition to misrank. (A default colour states nothing, as FromStated spelled it.)
        if (attributes.Flags.HasFlag(TextAttributes.Underline))
            style = style with { UnderlineColor = fgColor.IsDefault ? null : new SolidColorBrush(fgColor) };

        var glyphSource = new GlyphSource(Font, Sizing);
        var rtb = new RichTextBuilder(style, defaultGlyphSource: glyphSource);

        TextMarkup.Parse(s,
                         rtb,
                         new TextMarkupOptions
                         {
                             BrushResolver = ResourceBrushResolver.Create(this),
                             DefaultStyle = style
                         });

        return rtb.Build();
    }

    private void InvalidateContent(bool invalidateMeasure = false)
    {
        Cache.Invalidate();

        if (invalidateMeasure)
            InvalidateMeasure();

        InvalidateVisual();
    }

    private static void OnRenderAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as RichTextPresenter)?.InvalidateContent(invalidateMeasure: false);

    private static void OnLayoutAffectingPropertyChanged<T>(UIObject s, T oldValue, T newValue)
        => (s as RichTextPresenter)?.InvalidateContent(invalidateMeasure: true);
}