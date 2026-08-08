using System.Globalization;

using Cursorial.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Rendering.Media;
using Cursorial.Rendering.Text;
using Cursorial.Text;
using Cursorial.UI.Input;

namespace Cursorial.UI.Controls;

/// <summary>
/// The access-key label renderer (design doc §12.5): a never-templated leaf that draws its
/// <see cref="Text"/> and underlines the mnemonic grapheme (<see cref="KeyUnderlineProperty"/>,
/// default <see cref="UnderlineStyle.Single"/>) when <see cref="AccessKeyManager.ShowUnderlineProperty"/>
/// is set on it. Column math is grapheme-aware (<see cref="GraphemeWidth"/>).
/// </summary>
public sealed class AccessTextPresenter : UIElement
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

    static AccessTextPresenter()
    {
        // Like TextBlock, this is a direct text painter: a label change that measures to the same size
        // (e.g. "_Save" → "_Stop") must still repaint, so Text carries AffectsRender as well as
        // AffectsMeasure (the lanes are independent — doc §5.5).
        AffectsMeasure<AccessTextPresenter>(TextProperty);
        AffectsRender<AccessTextPresenter>(TextProperty, IndicatorBrushProperty, KeyWeightProperty, KeyInverseProperty, KeyUnderlineProperty);
    }

    private string? _cachedLabel;

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

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text.Text;
        _cachedLabel = null;
        return string.IsNullOrEmpty(text) ? Size.Empty : new Size(GraphemeWidth.StringWidth(text), 1);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var label = Text;
        var labelText = label.Text.Trim();
        
        if (string.IsNullOrEmpty(labelText) || finalSize.IsEffectivelyEmpty)
        {
            _cachedLabel = string.Empty;
            return finalSize;
        }

        var textWidth = GraphemeWidth.StringWidth(labelText);
        if (textWidth > finalSize.Columns)
        {
            // Cut by GRAPHEME CLUSTERS against the column budget — a char-index Substring reads a
            // display-column count as a UTF-16 length, which both throws for wide clusters (fewer
            // chars than columns) and can split a surrogate pair or emoji sequence.
            int budget = Math.Max(0, finalSize.Columns - GraphemeWidth.StringWidth(TextFormatter.DefaultEllipsis));
            _cachedLabel = $"{TakeColumns(labelText, budget)}{TextFormatter.DefaultEllipsis}";
            SetCurrentValue(TextBlock.IsTrimmedPropertyKey, true);
        }
        else
        {
            _cachedLabel = labelText;

            if (GetValueSource(TextBlock.IsTrimmedProperty) is { Kind: ValueSourceKind.Default, IsCurrentValue: true })
                ClearValue(TextBlock.IsTrimmedPropertyKey);
        }

        return finalSize;
    }

    /// <summary>The longest prefix of <paramref name="text"/> whose display width fits in
    /// <paramref name="columns"/> cells, cut at a grapheme-cluster boundary.</summary>
    private static string TakeColumns(string text, int columns)
    {
        int used = 0;
        int length = 0;
        ReadOnlySpan<char> remaining = text;

        while (!remaining.IsEmpty)
        {
            int len = StringInfo.GetNextTextElementLength(remaining);
            if (len <= 0) break;

            int width = GraphemeWidth.ClusterWidth(remaining[..len]);
            if (used + width > columns) break;

            used += width;
            length += len;
            remaining = remaining[len..];
        }

        return text[..length];
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var label = Text;
        var labelText = _cachedLabel/* ?? label.Text*/;

        if (string.IsNullOrEmpty(labelText) || context.Bounds.IsEffectivelyEmpty)
            return;

        // The effective TextElement attributes ride the content text, so a NoColor reverse-video state
        // (Inverse) carries onto the glyph cells too — matching the Border fill, for a uniform reversed face
        // (the caps-nocolor theme layer). None by default ⇒ no change for ordinary content. The underline
        // SHAPE rides the base style when present.
        var styleTemplate = StyleDeltaTemplate.FromElement(this);

        context.DrawText(0, 0, labelText, in styleTemplate);

        // The cue: underline the KeyIndex grapheme when AccessKeyManager.ShowUnderline is set on us.
        // The theme's ':access-keys AccessTextPresenter' rule flips ShowUnderline on EVERY presenter
        // under the cue-bearing root regardless of whether its label carries a mnemonic, so the HasKey
        // clause — not a false ShowUnderline — is what guarantees a mnemonic-less label draws no
        // underline even while the cue is active.
        if (!label.HasKey || label.KeyIndex >= labelText.Length || !AccessKeyManager.GetShowUnderline(this))
            return;

        var (column, cluster) = GraphemeAt(labelText, label.KeyIndex);
        if (cluster is null)
            return;

        // The cue as a VALUE (proposal-partial-style §11.4): two channels and one attribute, with
        // everything else inherited from the label's own style — which is exactly a PartialStyle. It is
        // built once here and applied to `baseTextStyle` at the paint below, rather than being folded
        // into a hand-assembled flag word.
        var cue = styleTemplate;

        var hasKeyUnderline = KeyUnderline is not null;
        var keyUnderlineStyle = KeyUnderline ?? UnderlineStyle.Single;

        // Reverse-video is a TOGGLE, not a union. If our normal presentation is already reverse-video
        // and the key is supposed to be too, the flag comes back OFF for the 'double-reverse-video'
        // effect — which is what the old `combined &= ~Inverse` special case spelled by hand, and what
        // the delta algebra says directly.
        if (KeyInverse)
            cue = cue.Toggling(TextAttributes.Inverse);

        // WEIGHT IS AN AXIS, AND THE CUE WINS. Bold and Faint share the SGR 22 reset, so `Bold | Faint`
        // is not "two attributes" — reaching it emits ESC[1m from a Faint predecessor and ESC[2m from a
        // Bold one, so the painted weight depends on whatever was painted before it (measured identically
        // in Kitty and Ghostty). The old `resolved.Flags | keyAttributes` reached it whenever a Faint
        // label carried a Bold cue, which the shipped Ansi16 theme produces (InteractiveCueWeight = Bold).
        // `Weighing` IMPOSES the cue's weight and clears the other, because the cue is the later and more
        // specific statement: it is the theme saying "this grapheme is the mnemonic", against a weight the
        // label states for its text as a whole.
        //
        // TextWeight.Normal is deliberately NOT imposed. It is the property's default and the value every
        // colour tier ships, so treating it as an opinion would have the resting cue strip the weight off
        // the mnemonic of every bold label — "no cue weight" is what Normal has always meant here.
        if (KeyWeight is not TextWeight.Normal)
            cue = cue.Weighing(KeyWeight);

        var indicatorBrush = IndicatorBrush ?? Foreground;
        var textBounds = new Rect(0, 0, GraphemeWidth.StringWidth(labelText), 1);

        // The underline rides the cue when the key states one, and also when the LABEL is underlined —
        // in which case the cue still owns the shape and the indicator colour over its own grapheme.
        // A shape implies the flag structurally (PartialStyle.ApplyTo), so no `Setting(Underline)` is
        // needed — and none is possible: Underline owns an axis, so WithSet/Setting reject it.
        if (hasKeyUnderline || styleTemplate.AppliedAttributes.HasFlag(TextAttributes.Underline))
            cue = cue.Underlining(keyUnderlineStyle, indicatorBrush);

        if (indicatorBrush is not null && hasKeyUnderline is false)
            cue = cue.WithForeground(indicatorBrush); // if no distinguishing cue, paint the entire marker

        context.DrawText(column, 0, cluster, cue, textBounds);
    }

    // Returns the display column and the grapheme cluster string at the given cluster index.
    private static (int Column, string? Cluster) GraphemeAt(string text, int index)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var column = 0;
        var i = 0;
        while (enumerator.MoveNext())
        {
            var cluster = (string)enumerator.Current;
            if (i == index)
                return (column, cluster);

            column += GraphemeWidth.ClusterWidth(cluster);
            i++;
        }

        return (0, null);
    }
}
