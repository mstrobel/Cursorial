using Cursorial.Rendering.Media;

namespace Cursorial.UI.Themes;

/// <summary>
/// The <c>"Theme.*"</c> resource-key naming convention (design doc §11.8 / §11.8a) — string constants that
/// are typo-proof from C# and verbatim from XAML. S8 adds control-specific keys here as its content lands.
/// <para>
/// <b>Cell-faithful role-token spine (design doc §11.8a; <c>default-theme-adoption-spec.md</c>):</b> the
/// default theme is <i>fill-bounded</i>, not line-bounded. Control identity is carried by these whole-cell
/// fill/foreground role tokens — never by stroked borders. Each is a <see cref="IBrush"/>
/// authored per <c>(ThemeBase, ColorDepth)</c> variant in <see cref="CursorialTheme"/>; control themes
/// <see cref="ResourceReference"/> into them via <see cref="Style.SetResource{T}"/>, so overriding one token at a
/// nearer chain scope — or a <c>RequestedThemeBase</c>/<c>RequestedColorTier</c> flip — re-skins every
/// default-look control with zero template work (the <b>R2</b> spine wiring is landed). The setters arm at the
/// <c>ControlTheme</c> layer (below <see cref="BindingPriority.LocalValue"/>), so an explicit local value the
/// consumer sets always wins.
/// </para>
/// <para>
/// <b>Focus model:</b> reverse-video for pickable controls (a <see cref="TextBrush"/>/<see cref="WindowBackground"/>
/// brush pair), an intensified <see cref="WellBrush"/> + caret for text controls — both pure paint-only flips,
/// no sub-cell ring. <see cref="BorderPen"/>/<see cref="FocusPen"/> are <b>opt-in chrome keys only</b> (for
/// <c>Border</c>/GroupBox/Window frames), not spine members. The <see cref="GlyphSetCarrier"/> keys are live
/// resource reads.
/// </para>
/// </summary>
public static class ThemeKeys
{
    // ───────────────────────────── role tokens (the cell-faithful spine, §11.8a) ─────────────────────────────

    /// <summary>Page/window background; also the reverse-video <i>text</i> color on a focused pick control.</summary>
    public const string WindowBackground = "Theme.WindowBackground";
    public const string WindowTitleBarBackground = "Theme.WindowTitleBarBackground";
    public const string WindowTitleBarActiveBackground = "Theme.WindowTitleBarActiveBackground";
    public const string WindowTitleBarActiveForeground = "Theme.WindowTitleBarActiveForeground";
    public const string WindowTitleBarForeground = "Theme.WindowTitleBarForeground";

    /// <summary>Resting control fill (button, field, header).</summary>
    public const string SurfaceBrush = "Theme.SurfaceBrush";

    /// <summary>Popup / list / grid / menu surface fill.</summary>
    public const string PanelBrush = "Theme.PanelBrush";

    /// <summary>Intensified fill for a focused <i>text</i> field.</summary>
    public const string WellBrush = "Theme.WellBrush";
    
    /// <summary>Fill for toolbars and similar.</summary>
    public const string ToolBarBrush = "Theme.ToolBarBrush";
    
    /// <summary>Fill for ribbons and similar.</summary>
    public const string RibbonBrush = "Theme.RibbonBrush";
    
    /// <summary>Selection fill (selected item/text) in a focused container.</summary>
    public const string SelectionBrush = "Theme.SelectionBrush";

    /// <summary>Selection ink (selected item/text) in a focused container.</summary>
    public const string SelectionInk = "Theme.SelectionInk";

    /// <summary>Selection fill in an <b>unfocused</b> container (the neutral-grey inactive selection, spec --sel-inactive).</summary>
    public const string SelectionInactiveBrush = "Theme.SelectionInactiveBrush";

    /// <summary>Even-row zebra fill in lists/grids (spec --altrow; consumed by the opt-in <c>:alternate</c> row look).</summary>
    public const string AlternateRowBrush = "Theme.AlternateRowBrush";

    /// <summary>Even-row zebra ink in lists/grids (spec --altrow-ink; consumed by the opt-in <c>:alternate</c> row look).</summary>
    public const string AlternateRowInk = "Theme.AlternateRowInk";

    /// <summary>Shared pointer-over (hover) fill.</summary>
    public const string HoverBrush = "Theme.HoverBrush";

    /// <summary>Shared pointer-over (hover) foreground</summary>
    public const string OnHoverBrush = "Theme.OnHoverBrush";

    /// <summary>Primary text / foreground; also the reverse-video <i>fill</i> on a focused pick control.</summary>
    public const string TextBrush = "Theme.TextBrush";

    /// <summary>Secondary (de-emphasized) text.</summary>
    public const string TextDimBrush = "Theme.TextDimBrush";

    /// <summary>Tertiary text and glyph ink.</summary>
    public const string MutedBrush = "Theme.MutedBrush";

    /// <summary>Inactive track / faint glyph (slider/scrollbar track, empty progress segment).</summary>
    public const string FaintBrush = "Theme.FaintBrush";

    /// <summary>Disabled control fill.</summary>
    public const string DisabledBackgroundBrush = "Theme.DisabledBackgroundBrush";

    /// <summary>Disabled text/glyph ink.</summary>
    public const string DisabledForegroundBrush = "Theme.DisabledForegroundBrush";

    /// <summary>Primary action color.</summary>
    public const string AccentBrush = "Theme.AccentBrush";

    /// <summary>Secondary action color.</summary>
    public const string Accent2Brush = "Theme.Accent2Brush";

    /// <summary>Darkened action color.</summary>
    public const string AccentDarkBrush = "Theme.AccentDarkBrush";

    /// <summary>Darkened action color.</summary>
    public const string AccentInverseBrush = "Theme.AccentInverseBrush";

    /// <summary>Primary navigation color.</summary>
    public const string CoolBrush = "Theme.CoolBrush";

    /// <summary>Secondary navigation color.</summary>
    public const string Cool2Brush = "Theme.Cool2Brush";

    /// <summary>Navigation darkened color.</summary>
    public const string CoolDarkBrush = "Theme.CoolDarkBrush";

    /// <summary>Navigation darkened color.</summary>
    public const string CoolInverseBrush = "Theme.CoolInverseBrush";

    /// <summary>First-degree warnings.</summary>
    public const string DangerBrush = "Theme.DangerBrush";

    /// <summary>Second-degree warnings.</summary>
    public const string Danger2Brush = "Theme.Danger2Brush";

    /// <summary>Darkened danger brush..</summary>
    public const string DangerDarkBrush = "Theme.DangerDarkBrush";

    /// <summary>Darkened danger brush..</summary>
    public const string DangerInverseBrush = "Theme.DangerInverseBrush";

    /// <summary>Text drawn on an accent/colored fill (pressed-button text, badge text).</summary>
    public const string OnAccentBrush = "Theme.OnAccentBrush";
    
    /// <summary>Text drawn on an dark accent/colored fill (pressed-button text, badge text).</summary>
    public const string OnAccentInverseBrush = "Theme.OnAccentInverseBrush";

    /// <summary>Success / on.</summary>
    public const string GreenBrush = "Theme.GreenBrush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string AmberBrush = "Theme.AmberBrush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string WarningBrush = "Theme.WarningBrush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string Warning2Brush = "Theme.Warning2Brush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string WarningDarkBrush = "Theme.WarningDarkBrush";

    /// <summary>Warning / paused / indeterminate mark.</summary>
    public const string WarningInverseBrush = "Theme.WarningInverseBrush";

    /// <summary>Informational color.</summary>
    public const string InfoBrush = "Theme.InfoBrush";

    /// <summary>Informational color.</summary>
    public const string Info2Brush = "Theme.Info2Brush";

    /// <summary>Informational color.</summary>
    public const string InfoDarkBrush = "Theme.InfoDarkBrush";

    /// <summary>Informational color.</summary>
    public const string InfoInverseBrush = "Theme.InfoInverseBrush";

    /// <summary>Success / on.</summary>
    public const string SuccessBrush = "Theme.SuccessBrush";
    
    /// <summary>Secondary success color.</summary>
    public const string Success2Brush = "Theme.Success2Brush";

    /// <summary>Darkened success color.</summary>
    public const string SuccessDarkBrush = "Theme.SuccessDarkBrush";

    /// <summary>Darkened success color.</summary>
    public const string SuccessInverseBrush = "Theme.SuccessInverseBrush";

    /// <summary>Color for special purposes.</summary>
    public const string SpecialBrush = "Theme.SpecialBrush";

    /// <summary>Error / danger.</summary>
    public const string RedBrush = "Theme.RedBrush";

    /// <summary>Pressed-slider thumb / visited link / file glyph.</summary>
    public const string PurpleBrush = "Theme.PurpleBrush";

    /// <summary>Status bar default background.</summary>
    public const string StatusBarBackground = "Theme.StatusBarBackground";

    /// <summary>Status bar alternate/branch background.</summary>
    public const string StatusBarAltBackground = "Theme.StatusBarAltBackground";

    /// <summary>Status bar alternate/branch foreground.</summary>
    public const string StatusBarAltForeground = "Theme.StatusBarAltForeground";

    /// <summary>Background at the Desktop elevation level.</summary>
    public const string ElevationDesktop = "Theme.ElevationDesktop";

    /// <summary>Background at the Popup elevation level.</summary>
    public const string ElevationPopup = "Theme.ElevationPopup";

    /// <summary>Background at the Window elevation level.</summary>
    public const string ElevationWindow = "Theme.ElevationWindow";

    /// <summary>Background at the Modal Dialog elevation level.</summary>
    public const string ElevationDialog = "Theme.ElevationDialog";

    /// <summary>Background at the Raised elevation level.</summary>
    public const string ElevationRaised = "Theme.ElevationRaised";
    
    /// <summary>Background at the Highest elevation level.</summary>
    public const string ElevationHighest = "Theme.ElevationHighest";
    
    /// <summary>Background at the Well (lowest) elevation level.</summary>
    public const string ElevationWell = "Theme.ElevationWell";

    // ───────────────────────────── opt-in chrome (NOT spine members, §11.8a) ─────────────────────────────

    /// <summary>
    /// Opt-in focus-ring pen — <b>not</b> the default focus mechanism (focus is reverse-video / well+caret).
    /// Retained for apps that re-introduce a bordered look; no common control reads it by default.
    /// </summary>
    public const string FocusPen = "Theme.FocusPen";

    /// <summary>
    /// Opt-in border pen — <b>not</b> a spine member (the cell-faithful default is fill-bounded). Survives for
    /// the line-drawn surfaces (<c>Border</c>/GroupBox/Expander/Window chrome) that genuinely want a frame.
    /// </summary>
    public const string BorderPen = "Theme.BorderPen";
    public const string FocusBorderPen = "Theme.FocusBorderPen";
    public const string ToolTipBorderPen = "Theme.ToolTipBorderPen";
    public const string MenuBorderPen = "Theme.MenuBorderPen";
    public const string TabControlBorderPen = "Theme.TabControlBorderPen";

    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Accent"/> class.</summary>
    public const string AccentBorderPen = "Theme.AccentBorderPen";
    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Info"/> class.</summary>
    public const string InfoBorderPen = "Theme.InfoBorderPen";
    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Cool"/> class.</summary>
    public const string CoolBorderPen = "Theme.CoolBorderPen";
    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Success"/> class.</summary>
    public const string SuccessBorderPen = "Theme.SuccessBorderPen";
    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Warning"/> class.</summary>
    public const string WarningBorderPen = "Theme.WarningBorderPen";
    /// <summary>The border pen to be used on dialogs with the <see cref="ThemeClass.Danger"/> class.</summary>
    public const string DangerBorderPen = "Theme.DangerBorderPen";
    
    /// <summary>
    /// Heavy pen used for the Separator control outside of menu contexts.
    /// </summary>
    public const string SeparatorPen = "Theme.SeparatorPen";

    /// <summary>
    /// Light pen used for the Separator control within menu contexts.
    /// </summary>
    public const string MenuSeparatorPen = "Theme.MenuSeparatorPen";

    /// <summary>The Slider's FILLED (value-side) rail — a Heavy <c>━</c> in the accent (design guide).</summary>
    public const string SliderFilledPen = "Theme.SliderFilledPen";

    /// <summary>The Slider's UNFILLED (empty) rail — a Heavy <c>━</c> in the faint ink (design guide).</summary>
    public const string SliderTrackPen = "Theme.SliderTrackPen";

    // ───────────────────────────── infrastructure (carried over) ─────────────────────────────

    /// <summary>The modal-dimming overlay brush (the <c>^.obscured</c> rule, design doc §11.8).</summary>
    public const string ObscuredOverlayBrush = "Theme.ObscuredOverlayBrush";

    /// <summary>The access-key underline brush (the <c>:access-keys</c> cue, design doc §11.8).</summary>
    public const string AccessKeyIndicatorBrush = "Theme.AccessKeyIndicatorBrush";

    /// <summary>
    /// The <see cref="Controls.CheckBox"/> glyph triple — a <see cref="GlyphSetCarrier"/> of
    /// <c>(Unchecked, Checked, Indeterminate)</c> strings (design doc §12.7; spec line 660). This resource
    /// is the caps-ascii <b>base</b> (<c>[ ] [x] [-]</c>); under <c>.caps-unicode</c> the marks are opted
    /// UP to colored Unicode (<c>[ ] [✓] [▪]</c>) via <c>ToggleGlyph.GlyphsProperty</c>, which is read
    /// <b>before</b> this resource and therefore WINS — so shadowing this resource at a nearer scope only
    /// takes effect on a caps-ascii terminal (caps-unicode is unconditionally stamped at P5; SD14).
    /// </summary>
    public const string CheckBoxGlyphs = "Theme.CheckBoxGlyphs";

    /// <summary>The <see cref="Controls.RadioButton"/> glyph triple (caps-ascii base <c>( ) (*) (-)</c>; caps-unicode opts up to <c>( ) (●) (-)</c> via the attached override — see <see cref="CheckBoxGlyphs"/>; design doc §12.7).</summary>
    public const string RadioGlyphs = "Theme.RadioGlyphs";

    /// <summary>The <see cref="Controls.ScrollBar"/> line-button arrow glyphs (the reserved ASCII base <c>^ v</c> / <c>&lt; &gt;</c>). NOTE: the BuiltIn ScrollBar template currently hardcodes Unicode arrows (◀▲▶▼) and does NOT yet read this resource — a caps-ascii opt-down is future work (same caps-ascii deferral as the Unicode check/radio marks; design doc §12.7).</summary>
    public const string ScrollArrowGlyphs = "Theme.ScrollArrowGlyphs";

    // ───────────────────────────── per-control override keys (style-guide KEYS, §11.4a) ─────────────────────────────
    //
    // The style-guide per-control resource keys (docs/ui-layer-design/tokyo-night-control-gallery-final.html, the
    // KEYS table). Each is a LIVE ALIAS (a ResourceReference, registered in CursorialTheme) of a palette role token,
    // so a control template references its OWN key while one role-token brush backs every consumer — and an app
    // re-keys a single control's brush at a nearer chain scope (e.g. `Resources[ThemeKeys.ButtonBackgroundNormal] =
    // myBrush`) to re-skin just that control, without disturbing the shared role token. Alias chasing
    // (ResourceExtensions §11.4a) resolves the indirection; a variant flip / role-token override still cascades
    // through the alias. The constant NAMES match the style guide so an app author finds them by the guide.

    // Base / shared — spec-named aliases of the existing role tokens (the 3 whose guide name differs from ours).
    /// <summary>Text on an accent/colored fill (spec <c>AccentForegroundBrush</c>, --on-accent) — aliases <see cref="OnAccentBrush"/>.</summary>
    public const string AccentForegroundBrush = "Theme.AccentForegroundBrush";
    /// <summary>Selection fill in a focused container (spec <c>SelectionActiveBrush</c>, --sel) — aliases <see cref="SelectionBrush"/>.</summary>
    public const string SelectionActiveBrush = "Theme.SelectionActiveBrush";
    /// <summary>Popup/list/grid/menu surface (spec <c>PanelBackgroundBrush</c>, --panel) — aliases <see cref="PanelBrush"/>.</summary>
    public const string PanelBackgroundBrush = "Theme.PanelBackgroundBrush";

    // Button (Button / RepeatButton / ToggleButton).
    /// <summary>Standard button ink (--text).</summary>
    public const string ButtonForegroundNormal = "Theme.ButtonForegroundNormal";
    /// <summary>Default button ink (--text).</summary>
    public const string ButtonForegroundDefault = "Theme.ButtonForegroundDefault";
    /// <summary>Resting button fill (--surface).</summary>
    public const string ButtonBackgroundNormal = "Theme.ButtonBackgroundNormal";
    /// <summary>Hovered button ink (--hover).</summary>
    public const string ButtonBackgroundHover = "Theme.ButtonBackgroundHover";
    /// <summary>Hovered button fill (--hover).</summary>
    public const string ButtonForegroundHover = "Theme.ButtonForegroundHover";
    /// <summary>Focused button ink — reverse-video (--bg).</summary>
    public const string ButtonForegroundFocus = "Theme.ButtonForegroundFocus";
    /// <summary>Focused button fill — reverse-video (--text).</summary>
    public const string ButtonBackgroundFocus = "Theme.ButtonBackgroundFocus";
    /// <summary>Pressed + IsDefault button ink (--on-accent).</summary>
    public const string ButtonForegroundPressed = "Theme.ButtonForegroundPressed";
    /// <summary>Pressed + IsDefault button fill (--accent).</summary>
    public const string ButtonBackgroundPressed = "Theme.ButtonBackgroundPressed";
    /// <summary>Disabled button ink (--muted).</summary>
    public const string ButtonForegroundDisabled = "Theme.ButtonForegroundDisabled";
    /// <summary>Disabled button fill (--disabled-bg).</summary>
    public const string ButtonBackgroundDisabled = "Theme.ButtonBackgroundDisabled";
    /// <summary>SplitButton dropdown indicator zone fill (--dd-zone).</summary>
    public const string SplitButtonDropZoneBrush = "Theme.SplitButtonDropZoneBrush";

    /// <summary>The reverse-video HALF of the per-tier interactive-cue pair (a <c>bool</c> — the
    /// per-axis split of the former whole-flags <c>InteractiveInverseAttributes</c>, proposal-
    /// textattributes-decomposition §2.3): <see langword="true"/> under NoColor (colors collapse to
    /// Default, so reverse-video restores the focus/pressed distinction — the one tier where
    /// attributes are the cue vocabulary), <see langword="false"/> at every color tier (the palette
    /// fill IS the cue; one cue vocabulary per tier). Cue rules set BOTH pair keys (the pair-coherence
    /// theme test walks every tier dictionary).</summary>
    public const string InteractiveCueInverse = "Theme.InteractiveCueInverse";

    /// <summary>The weight HALF of the per-tier interactive-cue pair (a <c>Cursorial.UI.Controls.TextWeight</c>):
    /// <c>Faint</c> at (Dark|Light, Ansi16) — the 16-color focus cue — and <c>Normal</c> everywhere else.
    /// Enum-typed so a future Bold-cue tier is a value change, not a third key.</summary>
    public const string InteractiveCueWeight = "Theme.InteractiveCueWeight";

    /// <summary>The underline style applied to the mnemonic grapheme of the access key cue.</summary>
    public const string InteractiveCueUnderline = "Theme.InteractiveCueUnderline";

    // ToggleSwitch / CheckBox / RadioButton.
    /// <summary>Check/radio glyph + label ink (--text).</summary>
    public const string ToggleForegroundNormal = "Theme.ToggleForegroundNormal";
    /// <summary>The CheckBox checked mark (--green).</summary>
    public const string ToggleGlyphChecked = "Theme.ToggleGlyphChecked";
    /// <summary>The tri-state indeterminate mark (--amber).</summary>
    public const string ToggleGlyphIndeterminate = "Theme.ToggleGlyphIndeterminate";
    /// <summary>The RadioButton checked dot (--accent; the radio variant of <see cref="ToggleGlyphChecked"/>).</summary>
    public const string RadioGlyphChecked = "Theme.RadioGlyphChecked";
    /// <summary>Disabled check/radio ink (--muted).</summary>
    public const string ToggleForegroundDisabled = "Theme.ToggleForegroundDisabled";

    // Input (TextBox, editable ComboBox, cell-edit).
    /// <summary>Input text ink (--text).</summary>
    public const string InputForegroundNormal = "Theme.InputForegroundNormal";
    /// <summary>Resting input fill (--surface).</summary>
    public const string InputBackgroundNormal = "Theme.InputBackgroundNormal";
    /// <summary>Hovered input fill (--hover).</summary>
    public const string InputBackgroundHover = "Theme.InputBackgroundHover";
    /// <summary>Hovered input ink (--text).</summary>
    public const string InputForegroundHover = "Theme.InputForegroundHover";
    /// <summary>Focused input fill — the recessed well (--well).</summary>
    public const string InputBackgroundFocus = "Theme.InputBackgroundFocus";
    /// <summary>Focused input ink.</summary>
    public const string InputForegroundFocus = "Theme.InputForegroundFocus";
    /// <summary>Input selection fill, control focused (--sel).</summary>
    public const string InputSelectionActive = "Theme.InputSelectionActive";
    /// <summary>Input selection fill, control unfocused (--sel).</summary>
    public const string InputSelectionInactive = "Theme.InputSelectionInactive";
    /// <summary>Disabled input ink (--muted).</summary>
    public const string InputForegroundDisabled = "Theme.InputForegroundDisabled";
    /// <summary>Disabled input fill (--disabled-bg).</summary>
    public const string InputBackgroundDisabled = "Theme.InputBackgroundDisabled";

    // ListItem (ListBox / ComboBox drop-down item).
    /// <summary>List-item ink (--text).</summary>
    public const string ListItemBackgroundNormal = "Theme.ListItemBackgroundNormal";
    /// <summary>List-item fill (--surface).</summary>
    public const string ListItemForegroundNormal = "Theme.ListItemForegroundNormal";
    /// <summary>Hovered list-item fill (--hover).</summary>
    public const string ListItemBackgroundHover = "Theme.ListItemBackgroundHover";
    /// <summary>Hovered list-item ink (--text).</summary>
    public const string ListItemForegroundHover = "Theme.ListItemForegroundHover";
    /// <summary>Selected list-item fill, focused container (--sel).</summary>
    public const string ListItemBackgroundSelected = "Theme.ListItemBackgroundSelected";
    /// <summary>Selected list-item ink.</summary>
    public const string ListItemForegroundSelected = "Theme.ListItemForegroundSelected";
    /// <summary>Selected but inactive list-item fill, focused container.</summary>
    public const string ListItemBackgroundSelectedInactive = "Theme.ListItemBackgroundSelectedInactive";
    /// <summary>Selected but inactive list-item ink.</summary>
    public const string ListItemForegroundSelectedInactive = "Theme.ListItemForegroundSelectedInactive";
    /// <summary>Keyboard-focused list-item ink — reverse-video.</summary>
    public const string ListItemForegroundFocus = "Theme.ListItemForegroundFocus";
    /// <summary>Keyboard-focused list-item fill — reverse-video (--text).</summary>
    public const string ListItemBackgroundFocus = "Theme.ListItemBackgroundFocus";
    /// <summary>Disabled list-item ink (--muted).</summary>
    public const string ListItemForegroundDisabled = "Theme.ListItemForegroundDisabled";
    /// <summary>Gutter glyph indicating selection.</summary>
    public const string ListItemSelectionGlyph = "Theme.ListItemSelectionGlyph";

    // TreeViewItem.
    /// <summary>Tree-node ink (--text).</summary>
    public const string TreeItemForegroundNormal = "Theme.TreeItemForegroundNormal";
    /// <summary>Selected tree-node fill (--sel).</summary>
    public const string TreeItemBackgroundSelected = "Theme.TreeItemBackgroundSelected";
    /// <summary>Keyboard-focused tree-node ink — reverse-video.</summary>
    public const string TreeItemForegroundFocus = "Theme.TreeItemForegroundFocus";
    /// <summary>Keyboard-focused tree-node fill — reverse-video (--text).</summary>
    public const string TreeItemBackgroundFocus = "Theme.TreeItemBackgroundFocus";
    /// <summary>Disabled tree-node ink (--muted).</summary>
    public const string TreeItemForegroundDisabled = "Theme.TreeItemForegroundDisabled";

    // Menu (MenuBar / MenuItem / ContextMenu).
    /// <summary>Menu-item ink (--text).</summary>
    public const string MenuForegroundNormal = "Theme.MenuForegroundNormal";
    /// <summary>The horizontal menu-bar strip fill (--surface).</summary>
    public const string MenuBarBackground = "Theme.MenuBarBackground";
    /// <summary>Hovered menu-item fill (--hover).</summary>
    public const string MenuBackgroundHover = "Theme.MenuBackgroundHover";
    /// <summary>Hovered menu-item fill (--hover).</summary>
    public const string MenuForegroundHover = "Theme.MenuForegroundHover";
    /// <summary>Highlighted / open menu-item fill (--sel).</summary>
    public const string MenuBackgroundHighlighted = "Theme.MenuBackgroundHighlighted";
    /// <summary>Highlighted / open menu-item ink</summary>
    public const string MenuForegroundHighlighted = "Theme.MenuForegroundHighlighted";
    /// <summary>The ^X accelerator/gesture hint text (--muted).</summary>
    public const string MenuAcceleratorForeground = "Theme.MenuAcceleratorForeground";
    /// <summary>The ^X accelerator/gesture hint text when hovered (--text-dim).</summary>
    public const string MenuAcceleratorHoverForeground = "Theme.MenuAcceleratorHoverForeground";
    /// <summary>Disabled menu-item ink (--muted).</summary>
    public const string MenuForegroundDisabled = "Theme.MenuForegroundDisabled";
    /// <summary>ink for <em>checkable</em> menu-item's checkmark/icon when <em>checked</em>.</summary>
    public const string MenuIconCheckedForeground = "Theme.MenuIconCheckedForeground";
    /// <summary>ink for <em>checkable</em> menu-item's checkmark/icon when <em>unchecked</em>.</summary>
    public const string MenuIconUncheckedForeground = "Theme.MenuIconUncheckedForeground";
    /// <summary>ink for <em>checkable, hovered</em> menu-item's checkmark/icon when <em>unchecked</em>.</summary>
    public const string MenuIconUncheckedHoverForeground = "Theme.MenuIconUncheckedHoverForeground";

    // TabItem.
    /// <summary>Inactive tab-header ink (--text-dim; the gallery dims unselected tabs).</summary>
    public const string TabForegroundNormal = "Theme.TabForegroundNormal";
    /// <summary>Active tab-header ink (--text).</summary>
    public const string TabForegroundSelected = "Theme.TabForegroundSelected";
    /// <summary>Hovered tab foreground.</summary>
    public const string TabForegroundHover = "Theme.TabForegroundHover";
    /// <summary>Hovered tab fill (--hover).</summary>
    public const string TabBackgroundHover = "Theme.TabBackgroundHover";
    /// <summary>Focused tab foreground.</summary>
    public const string TabForegroundFocused = "Theme.TabForegroundFocused";
    /// <summary>Focused tab fill (--focused).</summary>
    public const string TabBackgroundFocused = "Theme.TabBackgroundFocused";
    /// <summary>Selected/active tab fill (--surface).</summary>
    public const string TabBackgroundSelected = "Theme.TabBackgroundSelected";
    /// <summary>The active tab's accent underline rule — a Heavy <c>--accent</c> pen (the gallery "active tab marked by accent bar (━ cells)").</summary>
    public const string TabUnderlinePen = "Theme.TabUnderlinePen";
    /// <summary>The focused active tab's accent underline rule — a Heavy <c>--accent</c> pen (the gallery "active tab marked by accent bar (━ cells)").</summary>
    public const string TabFocusedUnderlinePen = "Theme.TabFocusedUnderlinePen";
    /// <summary>Disabled tab ink (--muted).</summary>
    public const string TabForegroundDisabled = "Theme.TabForegroundDisabled";

    // Ribbon (Cursorial.UI.Bars Surface B). The ribbon body reuses SurfaceBrush (--ribbon), the File tab reuses
    // Accent/OnAccent, group names + launcher reuse MutedBrush, large-button glyphs reuse Accent2Brush, and the
    // active-tab underline reuses TabUnderlinePen — only the tab-strip recess and the dropped-active-tab fill are new.
    /// <summary>The ribbon tab-strip background (--tabstrip — recessed behind the tabs).</summary>
    public const string RibbonTabStripBrush = "Theme.RibbonTabStripBrush";
    /// <summary>The active ribbon tab's dropped fill (--tab-active).</summary>
    public const string RibbonTabActiveBrush = "Theme.RibbonTabActiveBrush";
    /// <summary>The KeyTip badge fill (--keytip — the amber Alt-overlay accelerator badge background).</summary>
    public const string KeyTipBrush = "Theme.KeyTipBrush";
    /// <summary>The KeyTip badge text weight.</summary>
    public const string KeyTipTextWeight = "Theme.KeyTipTextWeight";
    /// <summary>The dimmed ink for a KeyTip badge's already-matched leading letters (multi-letter prefix highlight).</summary>
    public const string KeyTipMatchedBrush = "Theme.KeyTipMatchedBrush";
    /// <summary>A contextual ribbon tab's tinted resting well (--ctx-fill — the purple recess an inactive contextual
    /// tab sits in; the active one drops into <see cref="RibbonTabActiveBrush"/> but keeps its purple ink).</summary>
    public const string RibbonContextualFillBrush = "Theme.RibbonContextualFillBrush";
    /// <summary>The active contextual ribbon tab's underline (purple heavy — the <see cref="TabUnderlinePen"/> twin in
    /// --ctx; the sole active cue under the nocolor tier, so it is authored there too).</summary>
    public const string RibbonContextualUnderlinePen = "Theme.RibbonContextualUnderlinePen";

    // ProgressBar.
    /// <summary>Determinate progress fill (--green).</summary>
    public const string ProgressFillNormal = "Theme.ProgressFillNormal";
    /// <summary>Indeterminate progress sweep fill (--accent).</summary>
    public const string ProgressFillIndeterminate = "Theme.ProgressFillIndeterminate";
    /// <summary>Progress track fill.</summary>
    public const string ProgressTrackBrush = "Theme.ProgressTrackBrush";
    /// <summary>ScrollBar thumb normal fill.</summary>
    public const string ScrollBarThumbNormalBrush = "Theme.ScrollBarThumbNormalBrush";
    /// <summary>ScrollBar thumb hover fill.</summary>
    public const string ScrollBarThumbHoverBrush = "Theme.ScrollBarThumbHoverBrush";
    /// <summary>ScrollBar thumb pressed fill.</summary>
    public const string ScrollBarThumbDragBrush = "Theme.ScrollBarThumbDragBrush";
    /// <summary>ScrollBar track fill.</summary>
    public const string ScrollBarTrackBrush = "Theme.ScrollBarTrackBrush";

    // Calendar (day cells + Year/Decade cells; DatePicker).
    /// <summary>Calendar day-cell ink.</summary>
    public const string CalendarDayForeground = "Theme.CalendarDayForeground";
    /// <summary>Adjacent-month (inactive) day ink (--muted).</summary>
    public const string CalendarDayInactiveForeground = "Theme.CalendarDayInactiveForeground";
    /// <summary>Hovered day fill (--hover).</summary>
    public const string CalendarDayBackgroundHover = "Theme.CalendarDayBackgroundHover";
    /// <summary>Hovered day ink (--on-hover).</summary>
    public const string CalendarDayForegroundHover = "Theme.CalendarDayForegroundHover";
    /// <summary>Today's day ink (--accent).</summary>
    public const string CalendarDayTodayForeground = "Theme.CalendarDayTodayForeground";
    /// <summary>Selected day fill (--sel).</summary>
    public const string CalendarDayBackgroundSelected = "Theme.CalendarDayBackgroundSelected";
    /// <summary>Selected day ink (--text).</summary>
    public const string CalendarDayForegroundSelected = "Theme.CalendarDayForegroundSelected";
    /// <summary>Keyboard-focused day ink — reverse-video.</summary>
    public const string CalendarDayForegroundFocus = "Theme.CalendarDayForegroundFocus";
    /// <summary>Keyboard-focused day fill — reverse-video (--text).</summary>
    public const string CalendarDayBackgroundFocus = "Theme.CalendarDayBackgroundFocus";
    /// <summary>Disabled day ink (--muted).</summary>
    public const string CalendarDayForegroundDisabled = "Theme.CalendarDayForegroundDisabled";

    /// <summary>A brush meant to represent <see cref="Brushes.Black"/> (ANSI-16 palette index <c>0</c>).</summary>
    public const string AnsiBlack = "Theme.AnsiBlack";
    /// <summary>A brush meant to represent <see cref="Brushes.Red"/> (ANSI-16 palette index <c>1</c>).</summary>
    public const string AnsiRed = "Theme.AnsiRed";
    /// <summary>A brush meant to represent <see cref="Brushes.Green"/> (ANSI-16 palette index <c>2</c>).</summary>
    public const string AnsiGreen = "Theme.AnsiGreen";
    /// <summary>A brush meant to represent <see cref="Brushes.Yellow"/> (ANSI-16 palette index <c>3</c>).</summary>
    public const string AnsiYellow = "Theme.AnsiYellow";
    /// <summary>A brush meant to represent <see cref="Brushes.Blue"/> (ANSI-16 palette index <c>4</c>).</summary>
    public const string AnsiBlue = "Theme.AnsiBlue";
    /// <summary>A brush meant to represent <see cref="Brushes.Magenta"/> (ANSI-16 palette index <c>5</c>).</summary>
    public const string AnsiMagenta = "Theme.AnsiMagenta";
    /// <summary>A brush meant to represent <see cref="Brushes.Cyan"/> (ANSI-16 palette index <c>6</c>).</summary>
    public const string AnsiCyan = "Theme.AnsiCyan";
    /// <summary>A brush meant to represent <see cref="Brushes.White"/> (ANSI-16 palette index <c>7</c>).</summary>
    public const string AnsiWhite = "Theme.AnsiWhite";
    /// <summary>A brush meant to represent <see cref="Brushes.LightBlack"/> (ANSI-16 palette index <c>8</c>).</summary>
    public const string AnsiLightBlack = "Theme.AnsiLightBlack";
    /// <summary>A brush meant to represent <see cref="Brushes.LightRed"/> (ANSI-16 palette index <c>9</c>).</summary>
    public const string AnsiLightRed = "Theme.AnsiLightRed";
    /// <summary>A brush meant to represent <see cref="Brushes.LightGreen"/> (ANSI-16 palette index <c>10</c>).</summary>
    public const string AnsiLightGreen = "Theme.AnsiLightGreen";
    /// <summary>A brush meant to represent <see cref="Brushes.LightYellow"/> (ANSI-16 palette index <c>11</c>).</summary>
    public const string AnsiLightYellow = "Theme.AnsiLightYellow";
    /// <summary>A brush meant to represent <see cref="Brushes.LightBlue"/> (ANSI-16 palette index <c>12</c>).</summary>
    public const string AnsiLightBlue = "Theme.AnsiLightBlue";
    /// <summary>A brush meant to represent <see cref="Brushes.LightMagenta"/> (ANSI-16 palette index <c>13</c>).</summary>
    public const string AnsiLightMagenta = "Theme.AnsiLightMagenta";
    /// <summary>A brush meant to represent <see cref="Brushes.LightCyan"/> (ANSI-16 palette index <c>14</c>).</summary>
    public const string AnsiLightCyan = "Theme.AnsiLightCyan";
    /// <summary>A brush meant to represent <see cref="Brushes.LightWhite"/> (ANSI-16 palette index <c>15</c>).</summary>
    public const string AnsiLightWhite = "Theme.AnsiLightWhite";
}

/// <summary>
/// A small immutable carrier for a control's themeable glyph strings — a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
/// <c>(Unchecked, Checked, Indeterminate)</c> triple, a scroll-bar's arrow pair (design doc §12.7,
/// spec line 660). Glyphs are theme <b>resources</b> so a variant / capability dictionary can swap
/// them; the framework keys them under <see cref="ThemeKeys.CheckBoxGlyphs"/> /
/// <see cref="ThemeKeys.RadioGlyphs"/> / <see cref="ThemeKeys.ScrollArrowGlyphs"/>. The defaults are
/// true ASCII (zero ambiguous-width risk; cf. the ambiguous-width project memory).
/// </summary>
public record GlyphSetCarrier
{
    /// <summary>
    /// A small immutable carrier for a control's themeable glyph strings, such as a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
    /// <c>(Unchecked, Checked, Indeterminate)</c> triple or a scroll-bar's arrow pair. This allows glyphs to be defined as theme
    /// resources which can be dynamically swapped using a variant or capability dictionary. These glyphs are accessed using keys
    /// like <see cref="ThemeKeys.CheckBoxGlyphs"/>, <see cref="ThemeKeys.RadioGlyphs"/>, or <see cref="ThemeKeys.ScrollArrowGlyphs"/>.
    /// Default glyphs are designed to avoid ambiguous-width risks and typically leverage ASCII.
    /// </summary>
    public GlyphSetCarrier() {}

    /// <summary>
    /// A small immutable carrier for a control's themeable glyph strings — a <see cref="Cursorial.UI.Controls.CheckBox"/>'s
    /// <c>(Unchecked, Checked, Indeterminate)</c> triple, a scroll-bar's arrow pair (design doc §12.7,
    /// spec line 660). Glyphs are theme <b>resources</b> so a variant / capability dictionary can swap
    /// them; the framework keys them under <see cref="ThemeKeys.CheckBoxGlyphs"/> /
    /// <see cref="ThemeKeys.RadioGlyphs"/> / <see cref="ThemeKeys.ScrollArrowGlyphs"/>. The defaults are
    /// true ASCII (zero ambiguous-width risk; cf. the ambiguous-width project memory).
    /// </summary>
    /// <param name="unchecked">The first glyph (unchecked box / up-or-left arrow).</param>
    /// <param name="checked">The second glyph (checked box / down-or-right arrow).</param>
    /// <param name="indeterminate">The third glyph (the indeterminate box; unused for a two-glyph arrow pair).</param>
    public GlyphSetCarrier(string @unchecked, string @checked, string @indeterminate = "")
    {
        Unchecked = @unchecked;
        Checked = @checked;
        Indeterminate = @indeterminate;
    }

    /// <summary>
    /// Selects the glyph for a three-state checked value (<see langword="false"/>/<see langword="true"/>/
    /// <see langword="null"/>). When this set carries no third glyph (the two-argument constructor leaves
    /// <see cref="Indeterminate"/> empty), the indeterminate (<see langword="null"/>) value falls back to
    /// the <see cref="Unchecked"/> glyph — so the indeterminate state is visually indistinguishable from
    /// unchecked unless a third glyph is supplied. The built-in <c>RadioGlyphs</c>/<c>CheckBoxGlyphs</c>
    /// defaults supply a distinct indeterminate glyph.
    /// </summary>
    public string ForChecked(bool? value) => value switch
                                             {
                                                 true => Checked,
                                                 null => Indeterminate.Length > 0 ? Indeterminate : Unchecked,
                                                 _    => Unchecked
                                             };

    /// <summary>The first glyph (unchecked box / up-or-left arrow).</summary>
    public string Unchecked { get; init; } = "";

    /// <summary>The second glyph (checked box / down-or-right arrow).</summary>
    public string Checked { get; init; } = "";

    /// <summary>The third glyph (the indeterminate box; unused for a two-glyph arrow pair).</summary>
    public string Indeterminate { get; init; } = "";

    public void Deconstruct(out string @unchecked, out string @checked, out string indeterminate)
    {
        @unchecked = Unchecked;
        @checked = Checked;
        indeterminate = Indeterminate;
    }
}
