using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The access-key label renderer (design doc §12.5): a never-templated leaf that renders its
/// <see cref="Text"/> through the shared text pipeline (<see cref="FormattedTextCache"/> —
/// UNIFIED-TEXT-SCOPING M2) and marks the mnemonic grapheme with the access-key cue
/// (<see cref="KeyUnderlineProperty"/>, default <see cref="UnderlineStyle.Single"/>) when
/// <see cref="AccessKeyManager.ShowUnderlineProperty"/> is set on it.
/// </summary>
/// <remarks>
/// <para>
/// The cue is PIPELINE data (M2 addendum): the mnemonic cluster becomes a run wearing the cue's
/// <see cref="BrushedStyle"/> delta as its carrier, composed over the element's own style —
/// which rides whole as the document-default carrier (<see cref="BrushedStyle.FromElement"/>,
/// ruling M3's fallback shape). The plain-text fast path composes the same runs directly; the
/// fast≡slow equivalence matrix covers indicator-bearing inputs, so the two renderings cannot
/// drift. Both the carrier and the indicator are layout-key terms, so a cue or style change
/// re-formats by key miss on the repaint the property lanes already trigger.
/// </para>
/// <para>
/// Joining the pipeline is what gives the label real formatting ability: <see cref="TextWrapping"/>
/// (default <see cref="WrapMode.NoWrap"/> — the historical single-line behaviour) and
/// <see cref="TextTrimming"/> (default <see cref="Rendering.Text.TextTrimming.CharacterEllipsis"/>)
/// replace the hand-rolled single-line truncation. Column math stays grapheme-aware throughout.
/// </para>
/// </remarks>
public sealed class AccessTextPresenter : UIElement, ITrimmedTextSource
{
    /// <summary>The access-key label (<c>AffectsMeasure | AffectsRender</c> — a same-width label swap must repaint; see <see cref="TextBlock"/>).</summary>
    public static readonly StyledProperty<AccessText> TextProperty =
        UIProperty.Register<AccessTextPresenter, AccessText>(nameof(Text));

    /// <summary>The text weight applied to the mnemonic grapheme when the cue shows (default <see cref="TextWeight.Normal"/>; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<TextWeight> KeyWeightProperty =
        UIProperty.Register<AccessTextPresenter, TextWeight>(nameof(KeyWeight), defaultValue: TextWeight.Normal);

    /// <summary>The text reverse-video atrribute applied to the mnemonic grapheme when the cue shows (default <see langword="false"/>; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<bool> KeyInverseProperty =
        UIProperty.Register<AccessTextPresenter, bool>(nameof(KeyInverse), defaultValue: false);

    /// <summary>The underline style applied to the mnemonic grapheme when the cue shows (default <see cref="UnderlineStyle.Single"/>; <c>AffectsRender</c>).</summary>
    public static readonly StyledProperty<UnderlineStyle?> KeyUnderlineProperty =
        UIProperty.Register<AccessTextPresenter, UnderlineStyle?>(nameof(KeyUnderline),
                                                                  defaultValue: UnderlineStyle.Single);

    /// <summary>The text foreground — <see cref="TextElement.ForegroundProperty"/> <c>AddOwner</c> (inherits).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<AccessTextPresenter>();

    /// <summary>The foreground of the access key indicator (underline).</summary>
    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        UIProperty.Register<AccessTextPresenter, IBrush?>(nameof(IndicatorBrush));

    /// <summary>The wrap mode (<see cref="TextElement.TextWrappingProperty"/> <c>AddOwner</c>;
    /// default <see cref="WrapMode.NoWrap"/> — the label's historical single-line behaviour).</summary>
    public static readonly StyledProperty<WrapMode> TextWrappingProperty =
        TextElement.TextWrappingProperty.AddOwner<AccessTextPresenter>();

    /// <summary>The trimming mode for overflowing lines (<see cref="TextElement.TextTrimmingProperty"/>
    /// <c>AddOwner</c>; default <see cref="Rendering.Text.TextTrimming.CharacterEllipsis"/> — the
    /// label's historical ellipsis truncation).</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        TextElement.TextTrimmingProperty.AddOwner<AccessTextPresenter>();

    static AccessTextPresenter()
    {
        // Like TextBlock, this is a direct text painter: a label change that measures to the same size
        // (e.g. "_Save" → "_Stop") must still repaint, so Text carries AffectsRender as well as
        // AffectsMeasure (the lanes are independent — doc §5.5). The cue properties are AffectsRender:
        // they change the indicator DELTA, which is a layout-key term, so the repaint re-formats by key
        // miss without a re-measure (the cue never changes the label's geometry).
        AffectsMeasure<AccessTextPresenter>(TextProperty);
        AffectsRender<AccessTextPresenter>(TextProperty, IndicatorBrushProperty, KeyWeightProperty, KeyInverseProperty, KeyUnderlineProperty);
    }

    // The shared parse/format cache (UNIFIED-TEXT-SCOPING M2). Created lazily so no
    // base-constructor property plumbing can observe a null cache; internal so tests can observe
    // the fast-path routing counters.
    private FormattedTextCache? _cache;

    internal FormattedTextCache Cache
        => _cache ??= new FormattedTextCache(this, () =>
        {
            InvalidateMeasure();
            InvalidateVisual();
        });

    /// <summary>Creates an empty presenter.</summary>
    public AccessTextPresenter()
    {
    }

    /// <summary>Creates a presenter over <paramref name="text"/>.</summary>
    public AccessTextPresenter(AccessText text)
    {
        Text = text;
    }

    /// <inheritdoc cref="TextProperty"/>
    public AccessText Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <inheritdoc cref="KeyWeightProperty"/>
    public TextWeight KeyWeight { get => GetValue(KeyWeightProperty); set => SetValue(KeyWeightProperty, value); }

    /// <inheritdoc cref="KeyInverseProperty"/>
    public bool KeyInverse { get => GetValue(KeyInverseProperty); set => SetValue(KeyInverseProperty, value); }

    /// <inheritdoc cref="KeyUnderlineProperty"/>
    public UnderlineStyle? KeyUnderline { get => GetValue(KeyUnderlineProperty); set => SetValue(KeyUnderlineProperty, value); }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <inheritdoc cref="IndicatorBrushProperty"/>
    public IBrush? IndicatorBrush { get => GetValue(IndicatorBrushProperty); set => SetValue(IndicatorBrushProperty, value); }

    /// <inheritdoc cref="TextWrappingProperty"/>
    public WrapMode TextWrapping { get => GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }

    /// <inheritdoc cref="TextTrimmingProperty"/>
    public TextTrimming TextTrimming { get => GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
        => GetFormatted(Math.Max(1, availableSize.Columns), Math.Max(1, availableSize.Rows)).Size;

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // Format at the arranged budget and advertise trimming from it — TextBlock's spelling.
        var formatted = GetFormatted(Math.Max(1, finalSize.Columns), Math.Max(1, finalSize.Rows));
        SetValue(TextBlock.IsTrimmedPropertyKey, formatted.HasTrimmedLines);
        return finalSize;
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        if (context.Bounds.IsEffectivelyEmpty)
            return;

        var formatted = GetFormatted(Math.Max(1, context.Size.Columns), Math.Max(1, context.Size.Rows));
        if (formatted.Size.Rows == 0)
            return;

        // No paint preference: the element's whole style (attributes and brushes alike) IS the
        // document-default carrier, resolved per cell at the document's extent — so a NoColor
        // reverse-video state (Inverse) reaches the glyph cells like it always did, and a null
        // resolved foreground falls through to the terminal-default ink (Brushes.Default's value —
        // ruling M3's fallback).
        context.DrawFormattedText(formatted, context.Bounds);
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

    private FormattedText GetFormatted(int width, int? height)
    {
        var label = Text;

        // The label has always rendered whitespace-trimmed (the old arrange did the Trim); the
        // mnemonic's KeyIndex is applied against the trimmed text, as it always was.
        var text = label.Text?.Trim();

        if (string.IsNullOrEmpty(text))
            return FormattedText.Empty;

        // The element's whole style is the document-default carrier (M3's "current values as
        // fallbacks"), and the cue is an indicator DECLARATION riding the request — both are key
        // terms, so any change re-formats by key miss.
        var carrier = BrushedStyle.FromElement(this);
        var indicator = ComputeIndicator(label, in carrier);

        var request = new FormattedTextCache.LayoutRequest(
            text, MarkupLane: false, width, height,
            TextWrapping, TextAlignment.Left, TextTrimming,
            Carrier: carrier, Indicator: indicator);

        if (Cache.TryGetLayout(in request, out var cached))
            return cached;

        var formatted = Cache.TryFormatPlainTextFast(in request, in carrier)
                        ?? Cache.Format(
                               FormattedTextCache.BuildIndicatorText(text, in carrier, indicator,
                                                                     TextTrimming, TextWrapping),
                               width, height, TextAlignment.Left, TextTrimming, TextWrapping);
        Cache.StoreLayout(in request, formatted);
        return formatted;
    }

    /// <summary>
    /// The cue as a DELTA (proposal-partial-style §11.4): two channels and one attribute, with
    /// everything else inherited from the label's own style — the document-default carrier the
    /// delta composes over at paint. <see langword="null"/> when the cue is not showing, the label
    /// has no mnemonic, or the delta would be the identity.
    /// </summary>
    private FormattedTextCache.TextIndicator? ComputeIndicator(in AccessText label, in BrushedStyle labelStyle)
    {
        // The theme's ':access-keys AccessTextPresenter' rule flips ShowUnderline on EVERY presenter
        // under the cue-bearing root regardless of whether its label carries a mnemonic, so the HasKey
        // clause — not a false ShowUnderline — is what guarantees a mnemonic-less label draws no
        // underline even while the cue is active.
        if (!label.HasKey || !AccessKeyManager.GetShowUnderline(this))
            return null;

        var cue = BrushedStyle.Identity;

        var hasKeyUnderline = KeyUnderline is not null;
        var keyUnderlineStyle = KeyUnderline ?? UnderlineStyle.Single;

        // Reverse-video is a TOGGLE, not a union. If the label's normal presentation is already
        // reverse-video and the key is supposed to be too, the flag comes back OFF for the
        // 'double-reverse-video' effect — the delta algebra composing over the document carrier.
        if (KeyInverse)
            cue = cue.Toggling(TextAttributes.Inverse);

        // WEIGHT IS AN AXIS, AND THE CUE WINS. Bold and Faint share the SGR 22 reset, so `Bold | Faint`
        // is not "two attributes" — `Weighing` IMPOSES the cue's weight and clears the other, because
        // the cue is the later and more specific statement: it is the theme saying "this grapheme is
        // the mnemonic", against a weight the label states for its text as a whole.
        //
        // TextWeight.Normal is deliberately NOT imposed. It is the property's default and the value
        // every colour tier ships, so treating it as an opinion would have the resting cue strip the
        // weight off the mnemonic of every bold label — "no cue weight" is what Normal has always
        // meant here.
        if (KeyWeight is not TextWeight.Normal)
            cue = cue.Weighing(KeyWeight);

        var indicatorBrush = IndicatorBrush ?? Foreground;

        // The underline rides the cue when the key states one, and also when the LABEL is underlined —
        // in which case the cue still owns the shape and the indicator colour over its own grapheme.
        // A shape implies the flag structurally (PartialStyle.ApplyTo), so no `Setting(Underline)` is
        // needed — and none is possible: Underline owns an axis, so WithSet/Setting reject it.
        if (hasKeyUnderline || labelStyle.AppliedAttributes.HasFlag(TextAttributes.Underline))
            cue = cue.Underlining(keyUnderlineStyle, indicatorBrush);

        if (indicatorBrush is not null && hasKeyUnderline is false)
            cue = cue.WithForeground(indicatorBrush); // if no distinguishing cue, paint the entire marker

        if (cue.IsIdentity)
            return null;

        return new FormattedTextCache.TextIndicator(label.KeyIndex, cue);
    }

    // The trimmed-content tooltip payload (moved from ContentPresenter's inline copy — the
    // CharacterEllipsis untrimmed spelling TextBlock also uses; RTP/Figlet use Trim=None, and
    // unifying that choice is Mike-gated M4).
    string? ITrimmedTextSource.GetUntrimmedText(int maxWidth)
    {
        if (Text.Text is not { Length: > 0 } text)
            return null;

        var rt = new RichTextBuilder(defaultTrimming: TextTrimming.CharacterEllipsis,
                                     defaultWrap: WrapMode.CharacterWrap)
                .Run(text)
                .Build();

        var tf = new TextFormatter();

        var ft = tf.Format(rt, maxWidth, capabilities: UIApplication.Current?.Capabilities.Output);

        return ft.ToPlainText();
    }
}
