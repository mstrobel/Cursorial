using Cursorial.Output.Capabilities;
using Cursorial.Rendering;
using Cursorial.Rendering.Fonts;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// The leaf text element (design doc §12.7): renders <see cref="Text"/> (never access-key-folded) or
/// <see cref="Markup"/> (BBCode incl. <c>[fg=…]</c>/<c>[bg=…]</c> brush values via the S7 chain; wins over <see cref="Text"/>),
/// element-local through <see cref="RenderContext"/>. <see cref="Foreground"/> inherits via
/// <see cref="TextElement"/>. The <c>FormattedText</c> layout is cached in the shared
/// <see cref="FormattedTextCache"/>, keyed by
/// <c>(text/markup identity, width, wrap/alignment/trim, caps, resource version, ActualThemeVariant)</c> —
/// variant flips and renegotiates invalidate via the key, and a capability renegotiation also
/// pulses the cache's subscription; <b>no</b> dictionary subscription (sealed dictionaries
/// never pulse — CD16).
/// </summary>
public class TextBlock : UIElement, ITrimmedTextSource
{
    // Created lazily so no base-constructor property plumbing can observe a null cache.
    private FormattedTextCache? _cache;

    private FormattedTextCache Cache
        => _cache ??= new FormattedTextCache(this, () =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });

    /// <summary>The literal text content (<c>AffectsMeasure | AffectsRender</c>; never access-key-folded — doc §12.7).</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<TextBlock, string?>(nameof(Text));

    /// <summary>BBCode markup (<c>AffectsMeasure | AffectsRender</c>); wins over <see cref="Text"/> when both set (doc §12.7).</summary>
    public static readonly StyledProperty<string?> MarkupProperty =
        UIProperty.Register<TextBlock, string?>(nameof(Markup));

    /// <summary>The wrap mode (<c>AffectsMeasure | AffectsRender</c>).</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        TextElement.TextWrappingProperty.AddOwner<TextBlock>();

    /// <summary>The horizontal alignment of wrapped lines (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<TextBlock, TextAlignment>(nameof(TextAlignment),
                                                      defaultValue: TextAlignment.Left);

    /// <summary>The trimming mode for overflowing lines (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        TextElement.TextTrimmingProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="IsTrimmedProperty"/>
    internal static readonly UIPropertyKey<bool> IsTrimmedPropertyKey =
        UIProperty.RegisterReadOnly<TextBlock, bool>(nameof(IsTrimmed));

    /// <summary>Indicates whether any of the text content had trimming applied.</summary>
    public static readonly StyledProperty<bool> IsTrimmedProperty = IsTrimmedPropertyKey.Property;

    /// <summary>The text foreground — <see cref="TextElement.ForegroundProperty"/> <c>AddOwner</c> (inherits).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.TextWeightProperty"/>
    public static readonly StyledProperty<TextWeight> TextWeightProperty =
        TextElement.TextWeightProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.TextStyleProperty"/>
    public static readonly StyledProperty<TextStyle> TextStyleProperty =
        TextElement.TextStyleProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.UnderlineProperty"/>
    public static readonly StyledProperty<UnderlineStyle?> UnderlineProperty =
        TextElement.UnderlineProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.UnderlineBrushProperty"/>
    public static readonly StyledProperty<IBrush?> UnderlineBrushProperty =
        TextElement.UnderlineBrushProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.StrikethroughProperty"/>
    public static readonly StyledProperty<bool> StrikethroughProperty =
        TextElement.StrikethroughProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.OverlineProperty"/>
    public static readonly StyledProperty<bool> OverlineProperty =
        TextElement.OverlineProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.InverseProperty"/>
    public static readonly StyledProperty<bool> InverseProperty =
        TextElement.InverseProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.BlinkProperty"/>
    public static readonly StyledProperty<bool> BlinkProperty =
        TextElement.BlinkProperty.AddOwner<TextBlock>();

    /// <inheritdoc cref="TextElement.ConcealedProperty"/>
    public static readonly StyledProperty<bool> ConcealedProperty =
        TextElement.ConcealedProperty.AddOwner<TextBlock>();

    static TextBlock()
    {
        // The effects lanes are independent (doc §5.5 / PropertyEffects): AffectsMeasure routes to
        // InvalidateMeasure, AffectsRender to InvalidateVisual, with NO implication between them. A
        // re-measure only transitively re-rasters when the *arranged size* changes (UIElement.Layout
        // SetBoundsAndRoute), so for a direct text painter a same-size content change (a stretched
        // label, a fixed-width status line) measures identically and would never repaint unless the
        // content properties are ALSO AffectsRender. Text/Markup/TextWrapping change the painted glyphs
        // independently of size, so they carry both lanes.
        AffectsMeasure<TextBlock>(TextProperty, MarkupProperty, TextAlignmentProperty);
        AffectsRender<TextBlock>(TextProperty, MarkupProperty, TextAlignmentProperty);

        TextWrappingProperty.OverrideDefaultValue<TextBlock>(WrapMode.WordWrap);
    }

    /// <summary>Creates an empty text block.</summary>
    public TextBlock()
    {
    }

    /// <summary>Creates a text block over <paramref name="text"/>.</summary>
    public TextBlock(string? text)
    {
        Text = text;
    }

    /// <inheritdoc cref="TextWeightProperty"/>
    public TextWeight TextWeight { get => GetValue(TextWeightProperty); set => SetValue(TextWeightProperty, value); }
    
    /// <inheritdoc cref="TextStyleProperty"/>
    public TextStyle TextStyle { get => GetValue(TextStyleProperty); set => SetValue(TextStyleProperty, value); }
    
    /// <inheritdoc cref="UnderlineProperty"/>
    public UnderlineStyle? Underline { get => GetValue(UnderlineProperty); set => SetValue(UnderlineProperty, value); }
    
    /// <inheritdoc cref="UnderlineBrushProperty"/>
    public IBrush? UnderlineBrush { get => GetValue(UnderlineBrushProperty); set => SetValue(UnderlineBrushProperty, value); }
    
    /// <inheritdoc cref="StrikethroughProperty"/>
    public bool Strikethrough { get => GetValue(StrikethroughProperty); set => SetValue(StrikethroughProperty, value); }
    
    /// <inheritdoc cref="OverlineProperty"/>
    public bool Overline { get => GetValue(OverlineProperty); set => SetValue(OverlineProperty, value); }
    
    /// <inheritdoc cref="InverseProperty"/>
    public bool Inverse { get => GetValue(InverseProperty); set => SetValue(InverseProperty, value); }
    
    /// <inheritdoc cref="BlinkProperty"/>
    public bool Blink { get => GetValue(BlinkProperty); set => SetValue(BlinkProperty, value); }
    
    /// <inheritdoc cref="ConcealedProperty"/>
    public bool Concealed { get => GetValue(ConcealedProperty); set => SetValue(ConcealedProperty, value); }
    
    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <inheritdoc cref="MarkupProperty"/>
    public string? Markup { get => GetValue(MarkupProperty); set => SetValue(MarkupProperty, value); }

    /// <inheritdoc cref="TextWrappingProperty"/>
    public WrapMode TextWrapping { get => GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }

    /// <inheritdoc cref="TextAlignmentProperty"/>
    public TextAlignment TextAlignment { get => GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    /// <inheritdoc cref="TextTrimmingProperty"/>
    public TextTrimming TextTrimming { get => GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }

    /// <inheritdoc cref="IsTrimmedProperty"/>
    public bool IsTrimmed { get => GetValue(IsTrimmedProperty); protected set => SetValue(IsTrimmedPropertyKey, value); }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    
    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = GetFormatted(Math.Max(1, availableSize.Columns), Math.Max(1, availableSize.Rows));
        return formatted.Size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Clamp like MeasureOverride: a zero-row arrange is legal for a visible element (a
        // collapsed star row, a shrunken window) and the formatter throws on maxRows <= 0.
        var formatted = GetFormatted(Math.Max(1, finalSize.Columns), Math.Max(1, finalSize.Rows));
        SetValue(IsTrimmedPropertyKey, formatted.HasTrimmedLines);
        return finalSize;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEffectivelyEmpty)
            return;

        // Paint the same height-capped layout Arrange computed IsTrimmed from — the row-trim
        // ellipsis only exists in the capped format, and formatting unbounded here would both
        // paint a bare clip and thrash the single-slot cache against Arrange every frame.
        var formatted = GetFormatted(Math.Max(1, context.Size.Columns), Math.Max(1, context.Size.Rows));
        if (formatted.Size.Rows == 0)
            return;

        // The effective text attributes (Bold/Italic/Inverse/Faint/…) merge onto every painted cell at
        // paint time — NOT baked into the cached FormattedText (the attribute properties are
        // AffectsRender, so a flip re-paints the cached layout without re-formatting it). The fold is
        // the single composition point (proposal-TextAttributes-decomposition §3.1).
        var resolved = TextElement.ComposeAttributes(this);
        context.DrawFormattedText(formatted, context.Bounds,
                                  new BrushedStyle { Foreground = Foreground }
                                      .Imposing(resolved.Flags, resolved.UnderlineShape));
    }

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        Cache.Attach();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        Cache.Detach();
        base.OnDetachedFromTree(in e);
    }

    private FormattedText GetFormatted(int width, int? height = null)
    {
        // Markup wins over Text (doc §12.7), so the key's source is the effective one — a Text
        // change under a set Markup formats identically and may serve the cached layout. The lane
        // bit keeps a literal Text from serving a layout for an IDENTICAL markup string.
        var request = new FormattedTextCache.LayoutRequest(
            Markup ?? Text, MarkupLane: Markup is not null, width, height,
            TextWrapping, TextAlignment, TextTrimming);

        if (Cache.TryGetLayout(in request, out var cached))
            return cached;

        var formatted = Format(width, Cache.OutputCapabilities, height);
        Cache.StoreLayout(in request, formatted);
        return formatted;
    }

    private FormattedText Format(int width,
                                 OutputCapabilities? caps,
                                 int? maxHeight = null,
                                 TextTrimming? trimmingOverride = null,
                                 WrapMode? wrappingOverride = null)
    {
        RichText document;

        if (Markup is {} markup)
        {
            // Markup wins over Text (doc §12.7); [fg=…]/[bg=…] brush values resolve via the S7 chain.
            var options = new TextMarkupOptions
                          {
                              BrushResolver = ResourceBrushResolver.Create(this),
                              DefaultTextTrimming = trimmingOverride ?? TextTrimming,
                              DefaultTextWrapping = wrappingOverride ?? TextWrapping
                          };
            document = TextMarkup.Parse(markup, options);
        }
        else if (Text is { Length: > 0 } text)
        {
            document = BuildPlainText(text, trimmingOverride, wrappingOverride);
        }
        else
        {
            return FormattedText.Empty;
        }

        return Cache.Format(document, width, maxHeight, TextAlignment,
                             trimmingOverride ?? TextTrimming,
                             wrappingOverride ?? TextWrapping,
                             caps);
    }

    // Builds a single paragraph honoring hard line breaks (\r\n | \n | \r → LineBreak), per the
    // P2.6 text-tier behavior the matrix pins (C162).
    private RichText BuildPlainText(string text,
                                    TextTrimming? trimmingOverride = null,
                                    WrapMode? wrappingOverride = null)
    {
        // The document default declares no FOREGROUND: the element brush rides the paint preference
        // (RenderContent), which colors text no level of the document declared one for. Transparent's
        // stated foreground would out-rank it at the document rung and paint the glyphs transparent;
        // the transparent background and underline color stay stated (the compositing identity).
        var builder = new RichTextBuilder(defaultTrimming: trimmingOverride ?? TextTrimming,
                                          defaultWrap: wrappingOverride ?? TextWrapping,
                                          defaultStyle: Output.CellStyle.Transparent
                                                              .WithForeground(Cursorial.Media.Color.Default));

        return BuildPlainText(this, text, builder);
    }

    internal static RichText BuildPlainText(UIElement host, string text, RichTextBuilder builder)
    {
        GlyphSource? glyphSource = null;

        var font = TextElement.GetFont(host);

        if (TextElement.GetSizing(host) is { IsNormal: false } sizing &&
            UIApplication.Current?.EffectiveCapabilities.Output.TextSizing is { Scale: true })
        {
            glyphSource = new GlyphSource(font, sizing);
        }
        else if (font is not null && font != MonospaceFont.Default)
        {
            glyphSource = new GlyphSource(font);
        }

        // \r\n | \n | \r → LineBreak, with \r\n as ONE break — the shared splitter (D12: this fold
        // used to live only here while FigletPresenter split on the raw char pair).
        var first = true;

        foreach (var range in HardLineBreaks.EnumerateLines(text))
        {
            if (!first)
                builder.LineBreak();

            first = false;

            if (range.End.Value > range.Start.Value)
            {
                if (glyphSource != null)
                    builder.Run(text[range], glyphSource);
                else
                    builder.Run(text[range]);
            }
        }

        return builder.Build();
    }

    // The CharacterEllipsis untrimmed spelling (RTP/Figlet use Trim=None — the difference is
    // Mike-gated M4, so each implementation keeps its own).
    internal string? GetUntrimmedText(int maxWidth)
    {
        var formattedText = Format(maxWidth,
                                   UIApplication.Current?.EffectiveCapabilities.Output,
                                   trimmingOverride: TextTrimming.CharacterEllipsis,
                                   wrappingOverride: WrapMode.CharacterWrap);

        if (formattedText == FormattedText.Empty)
            return null;

        return formattedText.ToPlainText();
    }

    string? ITrimmedTextSource.GetUntrimmedText(int maxWidth) => GetUntrimmedText(maxWidth);
}
