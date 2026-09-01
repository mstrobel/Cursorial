using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Text;
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
        // style.SetResource(AccessTextPresenter.IndicatorBrushProperty, ThemeKeys.AccessKeyIndicatorBrush);
        return style;
    }

    internal static Style AccessKeyCueIndicatorStyle()
    {
        return new Style("AccessTextPresenter") { Key = "Theme.AccessKeyCueStyle"}
              .SetResource(AccessTextPresenter.IndicatorBrushProperty, ThemeKeys.AccessKeyIndicatorBrush)
              .SetResource(AccessTextPresenter.KeyInverseProperty, ThemeKeys.InteractiveCueInverse)
              .SetResource(AccessTextPresenter.KeyUnderlineProperty, ThemeKeys.InteractiveCueUnderline)
              .SetResource(AccessTextPresenter.KeyWeightProperty, ThemeKeys.InteractiveCueWeight);
    }

    internal static Style ActiveSelectionStyles()
    {
        return new Style(":is(ListBox):focus-within, :is(TreeView):focus-within, :is(ListView):focus-within")
               {
                   Key = "Theme.ActiveSelectionStyle",
                   Children =
                   {
                       new Style("^ :is(ListBoxItem):selected, " +
                                 "^ :is(TreeViewItem):selected, " +
                                 "^ :is(ListViewItem):selected")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemSelectedTextStyle),
                       new Style("^ :is(ListBoxItem):focus-visible:selected, " +
                                 "^ :is(TreeViewItem):focus-visible:selected, " +
                                 "^ :is(ListViewItem):focus-visible:selected")
                          .SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.ListItemFocusTextStyle)
                   }
               };
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
        var style = new Style("CheckBox") { Key = "Theme.CapsUnicode.CheckBox", RequiresCapabilities = StyleCapabilities.Unicode };
        style.Setters.Add(new Setter(ToggleGlyph.GlyphsProperty, new ResourceReference(ThemeKeys.UnicodeCheckBoxGlyphs)));
        return style;
    }

    /// <summary>The caps-unicode RadioButton glyph opt-up (<c>( ) / (●) / (-)</c>; ● accent); see <see cref="CapsUnicodeCheckBoxGlyphs"/>.</summary>
    internal static Style CapsUnicodeRadioGlyphs()
    {
        var style = new Style("RadioButton") { Key = "Theme.CapsUnicode.RadioButton", RequiresCapabilities = StyleCapabilities.Unicode };
        style.Setters.Add(new Setter(ToggleGlyph.GlyphsProperty, new ResourceReference(ThemeKeys.UnicodeRadioGlyphs)));
        return style;
    }

    /// <summary>
    /// The caps-nocolor INTERACTIVE-STATE layer (design doc §11.8a / adoption-spec §Q5): under
    /// <c>.caps-nocolor</c> every palette role resolves to <see cref="Colors.Default"/>, so the button
    /// family's color brush-pair states (<see cref="ControlThemes"/> <c>:focus</c>/<c>:pressed</c>/
    /// <c>:default</c>) are visually inert. This restores the distinction with a TEXT ATTRIBUTE instead:
    /// <see cref="TextAttributes.Inverse"/> reverse-videos the terminal's own default fg/bg. It is set on
    /// the control as the per-axis <see cref="TextElement.InverseProperty"/> (forwarded to the parts), which the template's
    /// Border fill AND the content text both honor (so the WHOLE face inverts, not just the glyphs). The
    /// subjects are the EXACT button-family types (Button / RepeatButton / ToggleButton) — CheckBox /
    /// RadioButton are excluded (they keep their in-box caret focus; a reverse fill would span the row).
    /// Armed at Theme layer.
    /// </summary>
    internal static Style CapsNoColorInteractiveInverse()
    {
        var style = new Style(":is(ButtonBase)")
                    {
                        Key = "Theme.CapsNoColor.Inverse",
                        RequiresCapabilities = StyleCapabilities.NoColor,
                        Children =
                        {
                            // per-axis (proposal §4.3); delivery = face forward + presenter forward
                            new Style("^:focus, ^:pointerover").Set(TextElement.InverseProperty, true),
                            // :pressed state inverts the reverse-video effect
                            new Style("^:pressed").Set(TextElement.InverseProperty, false)
                        }
                    };
        return style;
    }

    internal static Style CapsNoColorInputInteractiveInverse()
    {
        var style = new Style(":is(TextBox), :is(ProgressBar)")
                    { Key = "Theme.CapsNoColor.InputInverse", RequiresCapabilities = StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(TextElement.InverseProperty, true)); // per-axis (proposal §4.3); delivery = face forward + presenter forward
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
        var style = new Style(":is(ButtonBase):disabled")
                    { Key = "Theme.CapsNoColor.DisabledFaint", RequiresCapabilities = StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(TextElement.TextWeightProperty, TextWeight.Faint)); // the weight AXIS (proposal §1)
        return style;
    }

    /// <summary>
    /// The caps-nocolor SELECTION layer (companion to <see cref="CapsNoColorInteractiveInverse"/>): under
    /// <c>.caps-nocolor</c> the SelectionBrush resolves to <see cref="Colors.Default"/>, so a selected item is
    /// indistinguishable; the <c>Inverse</c> axis reverse-videos the row instead (the design guide's
    /// monochrome-selection rule). Subjects are CONTROL-level for every member — the axes are
    /// non-inheriting ("flows like Background", proposal §2.1), so a control-level value cannot leak
    /// into content: the TabItem member no longer needs the /template/ part targeting (the old
    /// "would invert its CONTENT as well" hazard dissolved with inheritance), and the historical
    /// TreeViewItem exclusion (inherited Inverse bleeding into nested children) is obsolete — the
    /// tree cue can land as a follow-up on the same pattern. Delivery: the face Border forwards
    /// Inverse from the control; the label rides the presenter forward. Armed at Theme layer.
    /// </summary>
    internal static Style CapsNoColorSelectionInverse()
    {
        var style = new Style(":is(ListBoxItem):focus, " +
                              ":is(ListBoxItem):selected, " +
                              ":is(ComboBoxItem):selected, " +
                              ":is(ComboBox):focus /template/ #PART_DropDown, " +
                              ":is(ComboBox):open /template/ #PART_DropDown, " +
                              ":is(DatePicker):focus-within /template/ #PART_DropDown, " +
                              ":is(DatePicker):open /template/ #PART_DropDown, " +
                              ":is(MenuItem):selected, " +
                              ":is(MenuItem):highlighted, " +
                              ":is(TabItem):focus")
        { Key = "Theme.CapsNoColor.SelectionInverse", RequiresCapabilities = StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(TextElement.InverseProperty, true));
        style.Setters.Add(new Setter(TextElement.TextWeightProperty, TextWeight.Bold));
        return style;
    }

    internal static Style CapsNoColorTreeSelectionInverse()
    {
        var style = new Style("TreeViewItem")
                    {
                        Key = "Theme.CapsNoColor.TreeSelectionInverse",
                        RequiresCapabilities = StyleCapabilities.NoColor,
                        Children =
                        {
                            new Style("^:selected").Set(TextElement.TextWeightProperty, TextWeight.Bold),
                            new Style("^:focus").Set(TextElement.InverseProperty, true)
                        }
                    };
        return style;
    }

    
    internal static Style CapsNoColorFocusBlink()
    {
        var style = new Style("Slider:focus /template/ Thumb")
        { Key = "Theme.CapsNoColor.FocusBlink", RequiresCapabilities = StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(TextElement.BlinkProperty, true));
        return style;
    }

    /// <summary>
    /// The caps-nocolor keyboard focus-row cue for list items (the deferred P9.3b "Inverse+Bold" —
    /// gallery <c>.item.rev</c>): under NoColor the brush-pair focus row collapses, so the row
    /// reverse-videos AND bolds. THE composability proof of the per-axis decomposition
    /// (proposal §5 P3): Inverse composes with the selection rule's Inverse (same axis, same value)
    /// while Bold rides the INDEPENDENT weight axis — the combined-flags rule that used to fight the
    /// selection rule is now two orthogonal contributions. Ordered after the selection rule.
    /// </summary>
    internal static Style CapsNoColorListFocusCue()
    {
        var style = new Style(":is(ListBoxItem):focus-visible, " +
                              ":is(ComboBoxItem):focus-visible, " +
                              ":is(MenuItem):focus-visible, " +
                              ":is(TabItem):focus-visible")
        { Key = "Theme.CapsNoColor.ListFocusCue", RequiresCapabilities = StyleCapabilities.NoColor };
        style.Setters.Add(new Setter(TextElement.InverseProperty, true));
        style.Setters.Add(new Setter(TextElement.TextWeightProperty, TextWeight.Bold));
        return style;
    }

    /// <summary>
    /// The interaction style for primary action buttons and other elements.
    /// </summary>
    internal static Style AccentStyle()
    {
        return new Style(".accent")
               {
                   Key = ThemeClassKeys.Accent,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.AccentBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.AccentBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.AccentBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.AccentBrush),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Accent2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.AccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.AccentDarkBrush)
                   }
               };
    }
    /// <summary>
    /// The interaction style for primary action buttons and other elements.
    /// </summary>
    internal static Style InfoStyle()
    {
        return new Style(".info")
               {
                   Key = ThemeClassKeys.Info,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.InfoBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.InfoBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.InfoBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.InfoBrush),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Info2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.InfoInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.InfoDarkBrush),
                       new Style("^Window")
                          .Set(Window.IconProperty, new IconCarrier("\uf05a", 2, null, "ℹ️", "ℹ")),
                       new Style("^Window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                       new Style("^Window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.AnsiCyan),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.AnsiLightCyan)
                   }
               };
    }

    /// <summary>
    /// The interaction style for primary navigation buttons and other elements.
    /// </summary>
    internal static Style CoolStyle()
    {
        return new Style(".cool")
               {
                   Key = ThemeClassKeys.Cool,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.CoolBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.CoolBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.CoolBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.CoolBrush),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Cool2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.CoolInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.CoolDarkBrush),
                       new Style("^Window")
                          .Set(Window.IconProperty, new IconCarrier("\uf059", 2, null, "❔", "︖")),
                       new Style("^Window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                       new Style("^Window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.CoolInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.CoolBrush)
                   }
               };
    }

    /// <summary>
    /// The interaction style for potentially destructive action buttons and other elements.
    /// </summary>
    internal static Style DangerStyle()
    {
        return new Style(".danger")
               {
                   Key = ThemeClassKeys.Danger,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.DangerBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.DangerBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.DangerBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.DangerBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.ButtonBackgroundNormal),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Danger2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.DangerInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.DangerDarkBrush),
                       new Style("^Window")
                          .Set(Window.IconProperty, new IconCarrier("\uf530", 2, null, "⛔️", "⛒")),
                       new Style("^Window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                       new Style("^Window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.DangerInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.DangerBrush)
                   }
               };
    }

    /// <summary>
    /// The interaction style for safe or successful action buttons and other elements.
    /// </summary>
    internal static Style SuccessStyle()
    {
        return new Style(".success")
               {
                   Key = ThemeClassKeys.Success,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.SuccessBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.SuccessBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.SuccessBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.SuccessBrush),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Success2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.SuccessInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.SuccessDarkBrush),
                       new Style("^Window")
                          .Set(Window.IconProperty, new IconCarrier("\uf058", 2, null, "✅", "✓")),
                       new Style("^Window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                       new Style("^Window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.SuccessInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.SuccessBrush)
                   }
               };
    }

    /// <summary>
    /// The interaction style for primary action buttons and other elements that need to convey warning.
    /// </summary>
    internal static Style WarningStyle()
    {
        return new Style(".warning")
               {
                   Key = ThemeClassKeys.Warning,
                   Children =
                   {
                       new Style("^TextBlock, ^Label")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.WarningBrush),
                       new Style("^ToolTip")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.WarningBorderPen)
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.WarningBrush),
                       new Style("^Button")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.WarningBrush),
                       new Style("^Button:pointerover")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.Warning2Brush),
                       new Style("^Button:focus-visible, ^Button:focus")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.WarningInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.TextBrush),
                       new Style("^Button:pressed")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Panel.BackgroundProperty, ThemeKeys.WarningDarkBrush),
                       new Style("^Window")
                          .Set(Window.IconProperty, new IconCarrier("\uf071", 2, null, "⚠️", "⚠")),
                       new Style("^Window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar :is(ButtonBase)")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush),
                       new Style("^Window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentInverseBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.WarningInverseBrush),
                       new Style("^Window:active-window /template/ Border#PART_TitleBar")
                          .SetResource(TextElement.ForegroundProperty, ThemeKeys.OnAccentBrush)
                          .SetResource(Border.BackgroundProperty, ThemeKeys.WarningBrush)
                   }
               };
    }

    /// <summary>
    /// Distinct style for separators that apear in menus.
    /// </summary>
    /// <returns></returns>
    internal static Style MenuSeparatorStyle()
    {
        return new Style("MenuItem Separator, Menu Separator") { Key = "Theme.MenuSeparator" }
           .SetResource(Control.BorderPenProperty, ThemeKeys.MenuBorderPen);
    }

    /// <summary>
    /// Forces borders to occlude when <c>.caps-nocolor</c> is active.
    /// </summary>
    internal static Style CapsNoColorBorderStyle()
    {
        // Owner-anchor FIDELITY (review): the template legs keep develop's owner shapes — Border chrome of
        // ANY owner, Panel chrome only of PANEL owners or the SURFACE ROOT (the root-owner legs ride the
        // still-stamped back-compat caps-* classes; "owner is a surface root" has no class-free selector).
        return new Style(":is(Border), " +
                         ":is(Panel), " +
                         ":is(UIElement) /template/ :is(Border), " +
                         ":is(Panel) /template/ :is(Panel), " +
                         ".caps-nocolor /template/ :is(Panel)")
               {
                   Key = "Theme.CapsNoColor.BorderStyle",
                   RequiresCapabilities = StyleCapabilities.NoColor,
                   // Children =
                   // {
                   //     new Style("^#PART_ObscuredOverlay")
                   //        .Set(Panel.OccludesProperty, false)
                   //        .Set(TextElement.TextWeightProperty, TextWeight.Faint)
                   //        .Set(Panel.BackgroundProperty, Brushes.Transparent)
                   // }
               }
              .Set(Panel.OccludesProperty, true)
              .Set(Panel.BackgroundProperty, Brushes.Default);
    }

    /// <summary>
    /// Forces borders to occlude when <c>.caps-ansi16</c> is active.
    /// </summary>
    internal static Style CapsAnsi16BorderStyle()
    {
        return new Style(":is(Border), " +
                         ":is(Panel), " +
                         ":is(UIElement) /template/ :is(Border), " +
                         ":is(Panel) /template/ :is(Panel), " +
                         ".caps-ansi16 /template/ :is(Panel)")
               { Key = "Theme.CapsAnsi16.BorderStyle", RequiresCapabilities = StyleCapabilities.Ansi16 } // key de-duplicated from the nocolor sibling (cosmetic: the styles channel is unkeyed — both rules armed under the old keys too)
           .Set(Panel.OccludesProperty, true)/*
           .Set(Panel.BackgroundProperty, Brushes.Default)*/;
    }

    /// <summary>
    /// Forces border visibility when <c>.caps-nocolor</c> is active.
    /// </summary>
    internal static Style CapsNoColorBorderPenStyle()
    {
        // The historical :is(Popup) leg is deleted (review): the structural Popup element renders nothing
        // and nothing reads BorderPen from it — popup-surface borders receive ThemeKeys.BorderPen through
        // their own control themes. The leg only added per-Popup DynamicResource subscriptions.
        return new Style(":is(Window), ContextMenu")
               { Key = "Theme.CapsNoColor.BorderPenStyle", RequiresCapabilities = StyleCapabilities.NoColor }
           .SetResource(Control.BorderPenProperty, ThemeKeys.BorderPen);
    }

    /// <summary>
    /// Forces border visibility when <c>.caps-ansi16</c> is active.
    /// </summary>
    internal static Style CapsAnsi16BorderPenStyle()
    {
        return new Style(":is(Window), ContextMenu")
               { Key = "Theme.CapsAnsi16.BorderPenStyle", RequiresCapabilities = StyleCapabilities.Ansi16 } // key de-duplicated from the nocolor sibling (cosmetic — see the nocolor twin's note)
           .SetResource(Control.BorderPenProperty, ThemeKeys.BorderPen);
    }

    internal static Style CapsNoColorMenuIconToggleStyle()
    {
        return new Style(":is(MenuItem)")
               {
                   Key = "Theme.BarToggleButton.CheckedInverse",
                   RequiresCapabilities = StyleCapabilities.NoColor,
                   Children =
                   {
                       new Style("^:checked /template/ #PART_Icon > Icon, " +
                                 "^:checked /template/ #PART_IconTray, " +
                                 "^:focus /template/ #PART_Icon > Icon, " +
                                 "^:focus /template/ #PART_IconTray, " +
                                 "^:pointerover /template/ #PART_Icon > Icon, " +
                                 "^:pointerover /template/ #PART_IconTray")
                          .Set(TextElement.InverseProperty, true),
                       new Style("^:focus:checked /template/ #PART_Icon > Icon, " +
                                 "^:focus:checked /template/ #PART_IconTray, " +
                                 "^:pointerover:checked /template/ #PART_Icon > Icon, " +
                                 "^:pointerover:checked /template/ #PART_IconTray, " +
                                 "^:focus:pressed /template/ #PART_Icon > Icon, " +
                                 "^:focus:pressed /template/ #PART_IconTray")
                          .Set(TextElement.InverseProperty, false)
                   }
               };
    }

    /// <summary>
    /// Replaces the translucent obscured root/window overlay with 'faint' content on <c>.caps-nocolor</c>.
    /// </summary>
    internal static Style CapsNoColorObscuredOverlayStyle()
    {
        // Owner-anchor FIDELITY (review): the template legs keep develop's owner shapes — Border chrome of
        // ANY owner, Panel chrome only of PANEL owners or the SURFACE ROOT (the root-owner legs ride the
        // still-stamped back-compat caps-* classes; "owner is a surface root" has no class-free selector).
        return new Style(":is(Window) /template/ Border#PART_ObscuredOverlay, " +
                         ":is(RootElementHost) > Border#PART_ObscuredOverlay")
               {
                   Key = "Theme.NoColor.ObscuredOverlayStyle",
                   RequiresCapabilities = StyleCapabilities.NoColor
               }.Set(Panel.OccludesProperty, false)
                .Set(TextElement.TextWeightProperty, TextWeight.Faint)
                .Set(Panel.BackgroundProperty, Brushes.Transparent);
    }

    /// <summary>
    /// Replaces the translucent obscured root/window overlay with 'faint' content on <c>.caps-ansi16</c>.
    /// </summary>
    internal static Style CapsAnsi16ObscuredOverlayStyle()
    {
        // Owner-anchor FIDELITY (review): the template legs keep develop's owner shapes — Border chrome of
        // ANY owner, Panel chrome only of PANEL owners or the SURFACE ROOT (the root-owner legs ride the
        // still-stamped back-compat caps-* classes; "owner is a surface root" has no class-free selector).
        return new Style(":is(Window) /template/ Border#PART_ObscuredOverlay, " +
                         ":is(RootElementHost) > Border#PART_ObscuredOverlay")
               {
                   Key = "Theme.CapsAnsi16.ObscuredOverlayStyle",
                   RequiresCapabilities = StyleCapabilities.Ansi16
               }.Set(Panel.OccludesProperty, false)
                .Set(TextElement.TextWeightProperty, TextWeight.Faint)
                .Set(Panel.BackgroundProperty, Brushes.Transparent);
    }

    internal static Style CapsAnsi16ThemeClassWindowBorders()
    {
        // Owner-anchor FIDELITY (review): the template legs keep develop's owner shapes — Border chrome of
        // ANY owner, Panel chrome only of PANEL owners or the SURFACE ROOT (the root-owner legs ride the
        // still-stamped back-compat caps-* classes; "owner is a surface root" has no class-free selector).
        return new Style(":is(Window)")
               {
                   Key = "Theme.CapsAnsi16.ThemeClassWindowBorders",
                   RequiresCapabilities = StyleCapabilities.Ansi16,
                   Children =
                   {
                       new Style("^.accent")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.AccentBorderPen),
                       new Style("^.info")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.InfoBorderPen),
                       new Style("^.cool")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.CoolBorderPen),
                       new Style("^.success")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.SuccessBorderPen),
                       new Style("^.warning")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.WarningBorderPen),
                       new Style("^.danger")
                          .SetResource(Control.BorderPenProperty, ThemeKeys.DangerBorderPen),
                   }
               };
    }

    internal static Style HeaderTextStyle()
    {
        // Owner-anchor FIDELITY (review): the template legs keep develop's owner shapes — Border chrome of
        // ANY owner, Panel chrome only of PANEL owners or the SURFACE ROOT (the root-owner legs ride the
        // still-stamped back-compat caps-* classes; "owner is a surface root" has no class-free selector).
        return new Style("TextBlock.header, AccessTextPresenter.header, RichTextPresenter.header")
               {
                   Key = "Theme.HeaderTextStyle"
               }.SetResource(TextElement.BaseTextStyleProperty, ThemeKeys.HeaderTextStyle);
    }
}
