using Cursorial.Drawing.Media;
using Cursorial.Rendering;
using Cursorial.Rendering.Text;

namespace Cursorial.UI.Controls;

/// <summary>
/// The leaf text element (design doc §12.7): renders <see cref="Text"/> (never access-key-folded) or
/// <see cref="Markup"/> (BBCode incl. <c>[brush=…]</c> via the S7 chain; wins over <see cref="Text"/>),
/// element-local through <see cref="RenderContext"/>. <see cref="Foreground"/> inherits via
/// <see cref="TextElement"/>. The <c>FormattedText</c> layout is cached keyed by
/// <c>(text/markup identity, width, caps, resource version, ActualThemeVariant)</c> — variant flips
/// and renegotiates invalidate via the key; <b>no</b> dictionary subscription (sealed dictionaries
/// never pulse — CD16).
/// </summary>
public class TextBlock : UIElement
{
    private FormattedText? _cached;
    private CacheKey _cacheKey;

    /// <summary>The literal text content (<c>AffectsMeasure | AffectsRender</c>; never access-key-folded — doc §12.7).</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<TextBlock, string?>(nameof(Text));

    /// <summary>BBCode markup (<c>AffectsMeasure | AffectsRender</c>); wins over <see cref="Text"/> when both set (doc §12.7).</summary>
    public static readonly StyledProperty<string?> MarkupProperty =
        UIProperty.Register<TextBlock, string?>(nameof(Markup));

    /// <summary>The wrap mode (<c>AffectsMeasure | AffectsRender</c>).</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        UIProperty.Register<TextBlock, WrapMode>(nameof(TextWrapping), defaultValue: WrapMode.NoWrap);

    /// <summary>The horizontal alignment of wrapped lines (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        UIProperty.Register<TextBlock, TextAlignment>(nameof(TextAlignment), defaultValue: TextAlignment.Left);

    /// <summary>The trimming mode for overflowing lines (<c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        UIProperty.Register<TextBlock, TextTrimming>(nameof(TextTrimming), defaultValue: TextTrimming.None);

    /// <summary>The text foreground — <see cref="TextElement.ForegroundProperty"/> <c>AddOwner</c> (inherits).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<TextBlock>();

    static TextBlock()
    {
        // The effects lanes are independent (doc §5.5 / PropertyEffects): AffectsMeasure routes to
        // InvalidateMeasure, AffectsRender to InvalidateVisual, with NO implication between them. A
        // re-measure only transitively re-rasters when the *arranged size* changes (UIElement.Layout
        // SetBoundsAndRoute), so for a direct text painter a same-size content change (a stretched
        // label, a fixed-width status line) measures identically and would never repaint unless the
        // content properties are ALSO AffectsRender. Text/Markup/TextWrapping change the painted glyphs
        // independently of size, so they carry both lanes.
        AffectsMeasure<TextBlock>(TextProperty, MarkupProperty, TextWrappingProperty, TextAlignmentProperty);
        AffectsRender<TextBlock>(TextProperty, MarkupProperty, TextWrappingProperty, TextAlignmentProperty, TextTrimmingProperty);
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

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = GetFormatted(Math.Max(1, availableSize.Columns));
        return formatted.Size;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEmpty)
            return;

        var formatted = GetFormatted(Math.Max(1, context.Size.Columns));
        if (formatted.Size.Rows == 0)
            return;

        // The inherited text attributes (Bold/Italic/Inverse/Faint/…) cascade via TextElement and merge onto
        // every painted cell at paint time — NOT baked into the cached FormattedText (TextAttributesProperty
        // is AffectsRender, so a flip re-paints the cached layout without re-formatting it).
        var attrs = TextElement.GetTextAttributes(this);
        if (Foreground is {} brush)
            context.DrawFormattedText(formatted, context.Bounds, brush, attrs);
        else
            context.DrawFormattedText(formatted, context.Bounds, attrs);
    }

    private FormattedText GetFormatted(int width)
    {
        var caps = UIApplication.Current is {} app ? app.Capabilities.Output : null;

        var key = new CacheKey(
            Text, Markup, width, TextWrapping, TextAlignment, TextTrimming,
            ResourceServices.GetResourceVersion(this),
            UIApplication.Current?.ActualThemeVariant,
            caps);

        if (_cached is {} cached && _cacheKey.Equals(key))
            return cached;

        var formatted = Format(width, caps);
        _cached = formatted;
        _cacheKey = key;
        return formatted;
    }

    private FormattedText Format(int width, Output.Capabilities.OutputCapabilities? caps)
    {
        var formatter = new TextFormatter
                        {
                            Wrap = TextWrapping,
                            Alignment = TextAlignment,
                            Trim = TextTrimming
                        };

        RichText document;
        if (Markup is {} markup)
        {
            // Markup wins over Text (doc §12.7); [brush=…] resolves via the S7 chain.
            var options = new TextMarkupOptions { BrushResolver = ResourceBrushResolver.Create(this) };
            document = TextMarkup.Parse(markup, options);
        }
        else if (Text is { Length: > 0 } text)
        {
            document = BuildPlainText(text);
        }
        else
        {
            return FormattedText.Empty;
        }

        if (document.IsEmpty)
            return FormattedText.Empty;

        return formatter.Format(document, width, capabilities: caps);
    }

    // Builds a single paragraph honoring hard line breaks (\r\n | \n | \r → LineBreak), per the
    // P2.6 text-tier behavior the matrix pins (C162).
    private static RichText BuildPlainText(string text)
    {
        var builder = new RichTextBuilder();
        var start = 0;

        text = text.Replace("[", "\\["); // Don't parse tags from plain text

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\n' or '\r'))
                continue;

            if (i > start)
                builder.Run(text[start..i]);
            builder.LineBreak();

            // Treat \r\n as one break.
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }

        if (start < text.Length)
            builder.Run(text[start..]);

        return builder.Build();
    }

    private readonly record struct CacheKey(
        string? Text,
        string? Markup,
        int Width,
        WrapMode Wrap,
        TextAlignment Alignment,
        TextTrimming Trim,
        int ResourceVersion,
        ThemeVariant? Variant,
        Output.Capabilities.OutputCapabilities? Capabilities);
}
