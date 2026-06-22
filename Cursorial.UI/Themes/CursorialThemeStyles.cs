using Cursorial.Output;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;

namespace Cursorial.UI.Themes;

/// <summary>
/// The selector styles the built-in theme ships through its <see cref="ResourceDictionary.Styles"/>
/// slot (design doc §11.8 #3) — consumed only from the application theme chain and armed at
/// <see cref="StyleLayer.Theme"/> (below <see cref="StyleLayer.App"/>, so an app style always wins).
/// </summary>
internal static class CursorialThemeStyles
{
    /// <summary>
    /// Requirement 6's access-key cue rule (design doc §7.8/§11.8): a descendant rule whose
    /// <c>:access-keys</c> pseudo-class binds on the <b>ancestor</b> scope/window root the
    /// <see cref="AccessKeyManager"/> stamps with <see cref="InteractionState.AccessKeyCue"/>, and
    /// whose subject is every <see cref="AccessTextPresenter"/> underneath. While the cue bit is up
    /// the rule flips each presenter's <see cref="AccessKeyManager.ShowUnderlineProperty"/>
    /// (<c>AffectsRender</c>), underlining the mnemonic grapheme; clearing the bit (Alt up / terminal
    /// focus out) retracts the frame and the underline vanishes. No per-control wiring, no inherited
    /// fan-out — the ancestor-state matcher (doc §3.3) reconciles only the presenters under the cue
    /// root. Permanently active in <see cref="AccessKeyMode.AlwaysVisible"/>; Alt-toggled in
    /// <see cref="AccessKeyMode.AltHeld"/>.
    /// </summary>
    internal static Style AccessKeyCue()
    {
        var style = new Style(":access-keys AccessTextPresenter") { Key = "Theme.AccessKeyCue" };
        style.Setters.Add(new Setter(AccessKeyManager.ShowUnderlineProperty, true));
        return style;
    }

    // NOTE on default text ink: a bare TextBlock takes its foreground from the inherited
    // TextElement.Foreground, which normally cascades from the root Window. A per-TextBlock theme-style
    // setting Foreground was rejected — it is a styled value ON the TextBlock and so DEFEATS a consumer's
    // inherited TextElement.Foreground (matrix C119). The inheritable default is established at the root
    // (the Window at P7; until then each demo sets TextElement.Foreground on its root element).

    /// <summary>
    /// The caps-unicode glyph opt-up (design doc §12.7 / SD14): under <c>.caps-unicode</c> (stamped on the
    /// root), opt the CheckBox marks UP from the ASCII resource base to colored Unicode by setting the
    /// <see cref="ToggleGlyph.GlyphsProperty"/> override — <c>[ ] / [✓] / [▪]</c>; ToggleGlyph colors the
    /// inner mark (✓ green / ▪ amber). A caps-ascii terminal never matches this and keeps the ASCII base.
    /// The genuinely capability-gated swap; armed at <see cref="StyleLayer.Theme"/>.
    /// </summary>
    internal static Style CapsUnicodeCheckBoxGlyphs()
    {
        var style = new Style(".caps-unicode CheckBox") { Key = "Theme.CapsUnicode.CheckBox" };
        style.Setters.Add(new Setter(ToggleGlyph.GlyphsProperty, (GlyphSetCarrier?)new GlyphSetCarrier("[ ]", "[✓]", "[▪]")));
        return style;
    }

    /// <summary>The caps-unicode RadioButton glyph opt-up (<c>( ) / (●) / (-)</c>; ● accent); see <see cref="CapsUnicodeCheckBoxGlyphs"/>.</summary>
    internal static Style CapsUnicodeRadioGlyphs()
    {
        var style = new Style(".caps-unicode RadioButton") { Key = "Theme.CapsUnicode.RadioButton" };
        style.Setters.Add(new Setter(ToggleGlyph.GlyphsProperty, (GlyphSetCarrier?)new GlyphSetCarrier("( )", "(●)", "(-)")));
        return style;
    }

    /// <summary>
    /// The caps-nocolor INTERACTIVE-STATE layer (design doc §11.8a / adoption-spec §Q5): under
    /// <c>.caps-nocolor</c> every palette role resolves to <see cref="Colors.Default"/>, so the button
    /// family's color brush-pair states (<see cref="ControlThemes"/> <c>:focus</c>/<c>:pressed</c>/
    /// <c>:default</c>) are visually inert. This restores the distinction with a TEXT ATTRIBUTE instead:
    /// <see cref="TextAttributes.Inverse"/> reverse-videos the terminal's own default fg/bg. It is set on
    /// the control as the inherited <see cref="TextElement.TextAttributesProperty"/>, which the template's
    /// Border fill AND the content text both honor (so the WHOLE face inverts, not just the glyphs). The
    /// subjects are the EXACT button-family types (Button / RepeatButton / ToggleButton) — CheckBox /
    /// RadioButton are excluded (they keep their in-box caret focus; a reverse fill would span the row).
    /// Armed at Theme layer.
    /// </summary>
    internal static Style CapsNoColorInteractiveInverse()
    {
        var style = new Style(
            ".caps-nocolor Button:focus, .caps-nocolor Button:pressed, .caps-nocolor Button:default, " +
            ".caps-nocolor RepeatButton:focus, .caps-nocolor RepeatButton:pressed, " +
            ".caps-nocolor ToggleButton:focus, .caps-nocolor ToggleButton:pressed")
        { Key = "Theme.CapsNoColor.Inverse" };
        style.Setters.Add(new Setter(TextElement.TextAttributesProperty, TextAttributes.Inverse));
        return style;
    }

    /// <summary>
    /// The caps-nocolor DISABLED layer (companion to <see cref="CapsNoColorInteractiveInverse"/>): under
    /// <c>.caps-nocolor</c> the disabled palette resolves to Default, so a disabled control is
    /// indistinguishable; <see cref="TextAttributes.Faint"/> dims it instead. Subject is
    /// <c>:is(ButtonBase):disabled</c> — every button-family control INCLUDING CheckBox/RadioButton.
    /// Armed at Theme layer.
    /// </summary>
    internal static Style CapsNoColorDisabledFaint()
    {
        var style = new Style(".caps-nocolor :is(ButtonBase):disabled") { Key = "Theme.CapsNoColor.DisabledFaint" };
        style.Setters.Add(new Setter(TextElement.TextAttributesProperty, TextAttributes.Faint));
        return style;
    }

    /// <summary>
    /// The caps-nocolor SELECTION layer (companion to <see cref="CapsNoColorInteractiveInverse"/>): under
    /// <c>.caps-nocolor</c> the SelectionBrush resolves to <see cref="Colors.Default"/>, so a selected item is
    /// indistinguishable; <see cref="TextAttributes.Inverse"/> reverse-videos the row instead (the design guide's
    /// monochrome-selection rule). Subjects are the FLAT input-list item types (the inherited TextAttributes would
    /// leak Inverse into a TreeViewItem's nested children, so the tree's header-bar reverse is left to a future
    /// part-targeted rule). Armed at Theme layer.
    /// </summary>
    internal static Style CapsNoColorSelectionInverse()
    {
        var style = new Style(
            ".caps-nocolor ListBoxItem:selected, .caps-nocolor ComboBoxItem:selected, .caps-nocolor TabItem:selected")
        { Key = "Theme.CapsNoColor.SelectionInverse" };
        style.Setters.Add(new Setter(TextElement.TextAttributesProperty, TextAttributes.Inverse));
        return style;
    }
}
