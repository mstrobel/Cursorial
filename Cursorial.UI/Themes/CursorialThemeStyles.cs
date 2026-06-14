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
}
