using Cursorial.Rendering.Media;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;
using Cursorial.UI.Themes;

namespace Cursorial.UI.Bars.Input;

/// <summary>
/// One amber KeyTip badge (keytips-design §8): an occluding <see cref="Control.Background"/>-filled chip carrying the
/// entry's <see cref="KeyTipText"/> letters over its target. The fill and inks come from the R2 DynamicResource
/// palette spine (<see cref="ThemeKeys.KeyTipBrush"/> / <see cref="ThemeKeys.KeyTipMatchedBrush"/>), so a dark/light
/// flip re-skins live. When a multi-letter prefix is typed, <see cref="MatchedPrefixLength"/> renders the matched
/// leading letters dimmed (matched ink) and the remainder in the resting ink. Badges only render when the terminal
/// has color (the ND23 gate blocks <c>AltHeld</c> under <c>NoColor</c>), so a dark ink on amber always contrasts.
/// </summary>
public sealed class KeyTipBadge : Control
{
    private const string PartMatched = "PART_Matched";
    private const string PartRest = "PART_Rest";

    /// <summary>The full badge letters (uppercased).</summary>
    public static readonly StyledProperty<string> KeyTipTextProperty =
        UIProperty.Register<KeyTipBadge, string>(nameof(KeyTipText), defaultValue: "",
                                                 changed: static (o, _, _) => (o as KeyTipBadge)?.UpdateText());

    /// <summary>How many leading letters have been matched by typed input (rendered dimmed).</summary>
    public static readonly StyledProperty<int> MatchedPrefixLengthProperty =
        UIProperty.Register<KeyTipBadge, int>(nameof(MatchedPrefixLength),
                                              changed: static (o, _, _) => (o as KeyTipBadge)?.UpdateText());

    private TextBlock? _matched;
    private TextBlock? _rest;

    /// <inheritdoc cref="KeyTipTextProperty"/>
    public string KeyTipText { get => GetValue(KeyTipTextProperty); set => SetValue(KeyTipTextProperty, value); }

    /// <inheritdoc cref="MatchedPrefixLengthProperty"/>
    public int MatchedPrefixLength { get => GetValue(MatchedPrefixLengthProperty); set => SetValue(MatchedPrefixLengthProperty, value); }

    static KeyTipBadge() => AffectsMeasure<KeyTipBadge>(KeyTipTextProperty, MatchedPrefixLengthProperty);

    /// <summary>Creates a badge, wiring its amber fill + inks to the theme palette and installing its template.</summary>
    public KeyTipBadge()
    {
        this.SetResourceReference(BackgroundProperty, ThemeKeys.KeyTipBrush);
        this.SetResourceReference(TextElement.TextWeightProperty, ThemeKeys.KeyTipTextWeight);
        SetValue(TemplateProperty, BuildTemplate());
    }

    private static ControlTemplate BuildTemplate() => new(ctx =>
    {
        var matched = new TextBlock();
        matched.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.KeyTipMatchedBrush);

        var rest = new TextBlock();
        // Dark-on-amber ink: the window background contrasts with the amber fill in BOTH themes (KeyTipBrush is
        // chosen to sit against it), so the letters stay legible on a dark/light flip.
        rest.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.WindowBackground);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { matched, rest } };

        var border = new Border { Occludes = true, Child = row };
        border.SetBinding(Border.BackgroundProperty,
                          TemplateBinding.From(BackgroundProperty)); // UIObject-rooted: the templated parent is the BADGE, not the Border

        TextElement.ForwardAllAxes(matched);
        TextElement.ForwardAllAxes(rest);
        
        ctx.RegisterName(PartMatched, matched);
        ctx.RegisterName(PartRest, rest);
        return border;
    });

    /// <inheritdoc/>
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _matched = GetTemplatePart<TextBlock>(PartMatched);
        _rest = GetTemplatePart<TextBlock>(PartRest);
        UpdateText();
    }

    // Splits the badge text at MatchedPrefixLength into the dimmed matched prefix + the resting remainder.
    private void UpdateText()
    {
        if (_matched is null || _rest is null)
            return;

        var text = KeyTipText ?? "";
        var split = Math.Clamp(MatchedPrefixLength, 0, text.Length);

        _matched.Text = text[..split];
        _matched.Visibility = split > 0 ? Visibility.Visible : Visibility.Collapsed;
        _rest.Text = text[split..];
    }
}
