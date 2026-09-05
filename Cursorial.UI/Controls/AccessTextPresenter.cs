using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Controls;

/// <summary>
/// The access-key label renderer (design doc §12.5): a never-templated leaf that renders its
/// <see cref="Text"/> through the shared text pipeline (<see cref="FormattedTextCache"/> —
/// UNIFIED-TEXT-SCOPING M2) and marks the mnemonic grapheme with the access-key cue
/// (<see cref="ActiveCueStyle"/>, default <see cref="UnderlineStyle.Single"/>) when
/// <see cref="AccessKeyManager.ShowUnderlineProperty"/> is set on it.
/// </summary>
/// <remarks>
/// <para>
/// The cue is PIPELINE data (M2 addendum): the mnemonic cluster becomes a run wearing the cue's
/// <see cref="BrushedStyle"/> delta as its carrier, composed over the element's own style —
/// which rides whole as the document-default carrier (<see cref="Extensions.FromElement"/>),
/// ruling M3's fallback shape). The plain-text fast path composes the same runs directly; the
/// fast≡slow equivalence matrix covers indicator-bearing inputs, so the two renderings cannot
/// drift. Both the carrier and the indicator are layout-key terms, so a cue or style change
/// re-formats by key miss on the repaint the property lanes already trigger.
/// </para>
/// <para>
/// Joining the pipeline is what gives the label real formatting ability: <see cref="TextWrapping"/>
/// (default <see cref="WrapMode.NoWrap"/> — the historical single-line behavior) and
/// <see cref="TextTrimming"/> (default <see cref="Rendering.Text.TextTrimming.CharacterEllipsis"/>)
/// replace the hand-rolled single-line truncation. Column math stays grapheme-aware throughout.
/// </para>
/// </remarks>
public sealed class AccessTextPresenter : UIElement, ITrimmedTextSource, IRichTextCapable
{
    /// <summary>The access-key label (<c>AffectsMeasure | AffectsRender</c> — a same-width label swap must repaint; see <see cref="TextBlock"/>).</summary>
    public static readonly StyledProperty<AccessText> TextProperty =
        UIProperty.Register<AccessTextPresenter, AccessText>(nameof(Text));

    /// <summary>The text style applied to an active access key cue.</summary>
    public static readonly StyledProperty<BrushedStyle> ActiveCueStyleProperty =
        UIProperty.Register<AccessTextPresenter, BrushedStyle>(
            nameof(ActiveCueStyle),
            new PropertyMetadata<BrushedStyle>{ DefaultResourceKey = ThemeKeys.InteractiveCueActiveStyle },
            inherits: true);

    /// <summary>The text style applied to an inactive access key cue.</summary>
    public static readonly StyledProperty<BrushedStyle> InactiveCueStyleProperty =
        UIProperty.Register<AccessTextPresenter, BrushedStyle>(
            nameof(InactiveCueStyle),
            new PropertyMetadata<BrushedStyle>{ DefaultResourceKey = ThemeKeys.InteractiveCueInactiveStyle },
            inherits: true);

    /// <summary>The text foreground — <see cref="TextElement.ForegroundProperty"/> <c>AddOwner</c> (inherits).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<AccessTextPresenter>();

    /// <summary>The wrap mode (<see cref="TextElement.TextWrappingProperty"/> <c>AddOwner</c>;
    /// default <see cref="WrapMode.NoWrap"/> — the label's historical single-line behavior).</summary>
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

        AffectsRender<AccessTextPresenter>(TextProperty, ActiveCueStyleProperty, InactiveCueStyleProperty);
    }

    // The shared parse/format cache (UNIFIED-TEXT-SCOPING M2). Created lazily so no
    // base-constructor property plumbing can observe a null cache; internal so tests can observe
    // the fast-path routing counters.
    internal FormattedTextCache Cache
        => field ??= new FormattedTextCache(this, () =>
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
    
    /// <inheritdoc cref="ActiveCueStyleProperty"/>
    public BrushedStyle ActiveCueStyle { get => GetValue(ActiveCueStyleProperty); set => SetValue(ActiveCueStyleProperty, value); }

    /// <inheritdoc cref="InactiveCueStyleProperty"/>
    public BrushedStyle InactiveCueStyle { get => GetValue(InactiveCueStyleProperty); set => SetValue(InactiveCueStyleProperty, value); }

    /// <inheritdoc cref="ForegroundProperty"/>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

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
        SetValue(TextElement.IsTrimmedPropertyKey, formatted.HasTrimmedLines);
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
        var text = label.Text.Trim();

        if (string.IsNullOrEmpty(text))
            return FormattedText.Empty;

        // The element's whole style is the document-default carrier (M3's "current values as
        // fallbacks"), and the cue is an indicator DECLARATION riding the request — both are key
        // terms, so any change re-formats by key miss.
        var carrier = BrushedStyle.FromElement(this);
        var cueStyle = ResolveCueStyle(in carrier);
        var indicator = ComputeIndicator(label, in cueStyle);

        var request = new FormattedTextCache.LayoutRequest(
            text, MarkupLane: false, width, height,
            TextWrapping, TextAlignment.Left, TextTrimming,
            Carrier: carrier, Indicator: indicator);

        if (Cache.TryGetLayout(in request, out var cached))
            return cached;

        var formatted = Cache.TryFormatPlainTextFast(in request, in carrier) ??
                        Cache.Format(
                            FormattedTextCache.BuildIndicatorText(text, in carrier, indicator,
                                                                  TextTrimming, TextWrapping),
                            width, height, TextAlignment.Left, TextTrimming, TextWrapping);

        Cache.StoreLayout(in request, formatted);
        return formatted;
    }

    private BrushedStyle ResolveCueStyle(in BrushedStyle labelStyle)
    {
        var active = AccessKeyManager.GetShowUnderline(this);
        var cue = active ? ActiveCueStyle : InactiveCueStyle;
        
        var underlineStyle = cue.UnderlineShape;

        if (cue.RemovedAttributes.HasFlag(TextAttributes.Underline))
            underlineStyle = null;

        var hasKeyUnderline = underlineStyle is not null;
        var keyUnderlineStyle = underlineStyle ?? UnderlineStyle.Single;

        var cueForeground = cue.Foreground ?? Foreground;
        var indicatorBrush = cue.UnderlineColor ?? cueForeground;

        // The underline rides the cue when the key states one, and also when the LABEL is underlined —
        // in which case the cue still owns the shape and the indicator color over its own grapheme.
        // A shape implies the flag structurally (PartialStyle.ApplyTo), so no `Setting(Underline)` is
        // needed — and none is possible: Underline owns an axis, so WithSet/Setting reject it.
        if (hasKeyUnderline || labelStyle.AppliedAttributes.HasFlag(TextAttributes.Underline))
            cue = cue.Underlining(keyUnderlineStyle, indicatorBrush ?? cueForeground);

        if (active && cue.Foreground is null && indicatorBrush is not null && hasKeyUnderline is false)
            cue = cue.WithForeground(indicatorBrush); // if no distinguishing cue, paint the entire marker

        if (indicatorBrush is not null && cue.AppliedAttributes.HasFlag(TextAttributes.Underline))
            cue = cue with { UnderlineColor = indicatorBrush };

        return cue;
    }

    /// <summary>
    /// The cue as a DELTA (proposal-partial-style §11.4): two channels and one attribute, with
    /// everything else inherited from the label's own style — the document-default carrier the
    /// delta composes over at paint. <see langword="null"/> when the cue is not showing, the label
    /// has no mnemonic, or the delta would be the identity.
    /// </summary>
    private FormattedTextCache.TextIndicator? ComputeIndicator(in AccessText label, in BrushedStyle cueStyle)
    {
        // The theme's ':access-keys AccessTextPresenter' rule flips ShowUnderline on EVERY presenter
        // under the cue-bearing root regardless of whether its label carries a mnemonic, so the HasKey
        // clause — not a false ShowUnderline — is what guarantees a mnemonic-less label draws no
        // underline even while the cue is active.
        if (!label.HasKey || cueStyle.IsIdentity /* i.e., cue is inactive and has no specific inactive presentation */)
            return null;

        return new FormattedTextCache.TextIndicator(label.KeyIndex, cueStyle);
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
