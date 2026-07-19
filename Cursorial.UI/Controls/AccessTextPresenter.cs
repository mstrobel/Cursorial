using System.Globalization;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.Text;
using Cursorial.UI.Input;

using CellStyle = Cursorial.Output.Style;

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
        return string.IsNullOrEmpty(text) ? Size.Empty : new Size(GraphemeWidth.StringWidth(text), 1);
    }

    /// <inheritdoc/>
    protected override void Render(RenderContext context)
    {
        var label = Text;
        if (string.IsNullOrEmpty(label.Text) || context.Bounds.IsEmpty)
            return;

        // The effective TextElement attributes ride the content text, so a NoColor reverse-video state
        // (Inverse) carries onto the glyph cells too — matching the Border fill, for a uniform reversed face
        // (the caps-nocolor theme layer). None by default ⇒ no change for ordinary content.
        var resolved = TextElement.ComposeAttributes(this);
        var baseTextStyle = new CellStyle().WithAttributes(resolved.Flags);
        // The underline SHAPE rides the base style when present (audit fix — Q2 no-silent-drops on the
        // DrawText path, not just the formatted-text seam); Single is the CellStyle default (a no-op).
        if ((resolved.Flags & TextAttributes.Underline) != 0)
            baseTextStyle = baseTextStyle.WithUnderlineStyle(resolved.UnderlineShape);

        var foreground = Foreground;
        if (foreground is {} brush)
            context.DrawText(0, 0, label.Text, brush, baseStyle: baseTextStyle);
        else
            context.DrawText(0, 0, label.Text, Color.Default, baseStyle: baseTextStyle);

        // The cue: underline the KeyIndex grapheme when AccessKeyManager.ShowUnderline is set on us.
        // The theme's ':access-keys AccessTextPresenter' rule flips ShowUnderline on EVERY presenter
        // under the cue-bearing root regardless of whether its label carries a mnemonic, so the HasKey
        // clause — not a false ShowUnderline — is what guarantees a mnemonic-less label draws no
        // underline even while the cue is active.
        if (!label.HasKey || !AccessKeyManager.GetShowUnderline(this))
            return;

        var (column, cluster) = GraphemeAt(label.Text, label.KeyIndex);
        if (cluster is null)
            return;

        var keyAttributes = default(TextAttributes);

        var hasKeyUnderline = KeyUnderline is not null;
        if (hasKeyUnderline)
            keyAttributes |= TextAttributes.Underline;

        if (KeyInverse)
            keyAttributes |= TextAttributes.Inverse;

        keyAttributes |= KeyWeight switch
                         {
                             TextWeight.Bold  => TextAttributes.Bold,
                             TextWeight.Faint => TextAttributes.Faint,
                             _                => default
                         };

        var keyUnderlineStyle = KeyUnderline ?? UnderlineStyle.Single;
        var combined = resolved.Flags | keyAttributes;

        // If our normal presentation is reverse-video, and the key is supposed to be reverse-video,
        // clear the flag for the 'double-reverse-video' effect.
        if (resolved.Flags.HasFlag(TextAttributes.Inverse) && keyAttributes.HasFlag(TextAttributes.Inverse))
            combined &= ~TextAttributes.Inverse;

        var hasUnderline = combined.HasFlag(TextAttributes.Underline);
        var style = new CellStyle().WithAttributes(combined);
        var indicatorBrush = IndicatorBrush ?? Foreground;
        
        if (indicatorBrush is not null)
        {
            if (hasUnderline)
            {
                style = style.WithUnderlineStyle(keyUnderlineStyle)
                             .WithUnderlineColor(indicatorBrush.ColorAt(column, 0, Bounds.ToRect()));
            }

            if (hasKeyUnderline is false)
                foreground = indicatorBrush; // if no distinguishing cue, paint the entire marker
        }

        if (foreground is {} fg)
            context.DrawText(column, 0, cluster, fg, baseStyle: style);
        else
            context.DrawText(column, 0, cluster, Color.Default, baseStyle: style);
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
